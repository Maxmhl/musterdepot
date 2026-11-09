using System.Text.Json;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Commands;
using Musterdepot.Core.Domain;
using Musterdepot.Core.ReadModels;
using static Musterdepot.Commands.Tests.TestHost;

namespace Musterdepot.Commands.Tests;

public sealed class CommandBusTests
{
    [Fact]
    public async Task Idempotency_SameKey_ReturnsDuplicateWithSameData()
    {
        await using var h = await CreateAsync();
        var first = await h.Bus.SendAsync(Buy(AutoAi, "k1"));
        var second = await h.Bus.SendAsync(Buy(AutoAi, "k1"));

        Assert.Equal(CommandStatus.Executed, first.Status);
        Assert.Equal(CommandStatus.Duplicate, second.Status);
        Assert.True(second.Success);
        Assert.Equal(((Order)first.Data!).OrderId, ((Order)second.Data!).OrderId);
        Assert.Single(h.Engine.Executed.OfType<PlaceOrderRequest>());

        // Gleicher Schlüssel, anderer Client → eigener Befehl.
        var other = await h.Bus.SendAsync(Buy("ui", "k1"));
        Assert.Equal(CommandStatus.Executed, other.Status);
        Assert.Equal(2, h.Engine.Executed.OfType<PlaceOrderRequest>().Count());
    }

    [Fact]
    public async Task Idempotency_ConcurrentDuplicates_ExecuteOnce()
    {
        await using var h = await CreateAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => h.Bus.SendAsync(Buy(AutoAi, "same"))));
        Assert.Single(results, r => r.Status == CommandStatus.Executed);
        Assert.Equal(7, results.Count(r => r.Status == CommandStatus.Duplicate));
        Assert.Single(h.Engine.Executed.OfType<PlaceOrderRequest>());
    }

    [Fact]
    public async Task Idempotency_KeyReusedForOtherCommand_IsRejected()
    {
        await using var h = await CreateAsync();
        await h.Bus.SendAsync(Buy(AutoAi, "k2"));
        var r = await h.Bus.SendAsync(new CancelAllOrdersCommand { Source = CommandSource.Ai, ClientId = AutoAi, IdempotencyKey = "k2" });
        Assert.Equal(CommandStatus.Rejected, r.Status);
    }

    [Fact]
    public async Task Idempotency_SurvivesRestart_ViaEngineClientOrderId()
    {
        await using var h = await CreateAsync();
        h.Engine.Execute(new PlaceOrderRequest { AccountId = Acc, ClientOrderId = "old-key", Symbol = "BTC/EUR", Side = OrderSide.Buy, Type = OrderType.Market, Quantity = 1 });
        var r = await h.Bus.SendAsync(Buy("ui", "old-key"));
        Assert.Equal(CommandStatus.Duplicate, r.Status);
        Assert.Single(h.Engine.Executed.OfType<PlaceOrderRequest>());
    }

    [Fact]
    public async Task UnknownClient_And_SourceMismatch_AreDenied()
    {
        await using var h = await CreateAsync();
        Assert.Equal(CommandStatus.Denied, (await h.Bus.SendAsync(Buy("unbekannt"))).Status);
        var humanWithAiId = Buy(AutoAi) with { Source = CommandSource.Human };
        Assert.Equal(CommandStatus.Denied, (await h.Bus.SendAsync(humanWithAiId)).Status);
        var aiAsUi = Buy("ui") with { Source = CommandSource.Ai };
        Assert.Equal(CommandStatus.Denied, (await h.Bus.SendAsync(aiAsUi)).Status);
        Assert.Empty(h.Engine.Executed.OfType<PlaceOrderRequest>());
    }

    [Fact]
    public async Task MissingScope_IsDenied()
    {
        await using var h = await CreateAsync();
        var settings = h.Get<SettingsService>().Current;
        var r = await h.Bus.SendAsync(new UpdateSettingsCommand { Source = CommandSource.Ai, ClientId = AutoAi, Settings = settings with { Theme = "dark" } });
        Assert.Equal(CommandStatus.Denied, r.Status);
        Assert.Contains("Settings", r.Message);
        var ro = await h.Bus.SendAsync(Buy(ReadOnlyAi));
        Assert.Equal(CommandStatus.Denied, ro.Status);
    }

    [Fact]
    public async Task HumanOnly_CommandsAreDeniedForAi()
    {
        await using var h = await CreateAsync();
        var ai = new[]
        {
            (ICommand)new DepositCashCommand { Source = CommandSource.Ai, ClientId = SettingsAi, Amount = 100 },
            new UpdateRiskLimitsCommand { Source = CommandSource.Ai, ClientId = SettingsAi, Risk = RiskConfig.Default },
            new SetClientModeCommand { Source = CommandSource.Ai, ClientId = SettingsAi, TargetClientId = ReadOnlyAi, Mode = ClientMode.Autonomous },
            new ReleaseKillSwitchCommand { Source = CommandSource.Ai, ClientId = SettingsAi },
            new ResolveApprovalCommand { Source = CommandSource.Ai, ClientId = SettingsAi, ApprovalId = "x", Approved = true },
        };
        foreach (var c in ai)
            Assert.Equal(CommandStatus.Denied, (await h.Bus.SendAsync(c)).Status);

        var deposit = await h.Bus.SendAsync(new DepositCashCommand { Amount = 100 });
        Assert.Equal(CommandStatus.Executed, deposit.Status);
        Assert.Equal(100_100m, h.Engine.GetPortfolio(Acc).Cash);
    }

    [Fact]
    public async Task ReadOnlyMode_DeniesWrites_AllowsDryRunAndKillSwitch()
    {
        await using var h = await CreateAsync();
        Assert.Equal(CommandStatus.Denied, (await h.Bus.SendAsync(Buy(ReadOnlyAi))).Status);
        Assert.Equal(CommandStatus.Denied, (await h.Bus.SendAsync(new CreateWatchlistCommand { Source = CommandSource.Ai, ClientId = ReadOnlyAi, Name = "x" })).Status);

        var preview = await h.Bus.SendAsync(Buy(ReadOnlyAi, dryRun: true));
        Assert.Equal(CommandStatus.Previewed, preview.Status);
        Assert.IsType<OrderPreview>(preview.Data);

        var ks = await h.Bus.SendAsync(new EngageKillSwitchCommand { Source = CommandSource.Ai, ClientId = ReadOnlyAi, Reason = "Gefahr" });
        Assert.Equal(CommandStatus.Executed, ks.Status);
        Assert.True(h.Engine.IsKillSwitchEngaged(Acc));
    }

    [Fact]
    public async Task DryRun_HasNoSideEffects_AndNeedsNoApproval()
    {
        await using var h = await CreateAsync();
        var before = h.Engine.Executed.Count;
        var r = await h.Bus.SendAsync(Buy(ConfirmAi, "dry", dryRun: true));
        Assert.Equal(CommandStatus.Previewed, r.Status);
        Assert.Equal(before, h.Engine.Executed.Count);
        Assert.Equal(1, h.Engine.PreviewCount);
        Assert.Empty(h.Engine.GetOrders(Acc));
        Assert.Empty(h.Notifier.ApprovalRequests);
        Assert.Empty(h.Get<IApprovalService>().Pending);
        Assert.Equal(0, h.Get<ClientActivityTracker>().OrdersLastMinute(ConfirmAi));

        // Gleicher Schlüssel danach als echte Order → wird ausgeführt (dry_run wird nicht zwischengespeichert).
        var approvals = h.Get<IApprovalService>();
        approvals.RequestAdded += (_, req) => approvals.Resolve(req.ApprovalId, true, "Tester");
        var real = await h.Bus.SendAsync(Buy(ConfirmAi, "dry"));
        Assert.Equal(CommandStatus.Executed, real.Status);
    }

    [Fact]
    public async Task ConfirmMode_Approve_ExecutesAndRecordsApprover()
    {
        await using var h = await CreateAsync();
        var approvals = h.Get<IApprovalService>();
        approvals.RequestAdded += (_, req) => _ = Task.Run(() => h.Bus.SendAsync(new ResolveApprovalCommand { ApprovalId = req.ApprovalId, Approved = true }));

        var r = await h.Bus.SendAsync(Buy(ConfirmAi, "c1"));
        Assert.Equal(CommandStatus.Executed, r.Status);
        Assert.NotNull(r.ApprovalId);
        Assert.Single(h.Notifier.ApprovalRequests);
        Assert.Contains("Kauf", h.Notifier.ApprovalRequests.Single().Summary);
        Assert.Single(h.Engine.GetOrders(Acc));

        var audit = await h.Get<IAuditLog>().QueryAsync(clientId: ConfirmAi);
        var entry = Assert.Single(audit);
        Assert.Equal("Oberfläche", entry.ApprovedBy);
        Assert.Equal(r.AuditId, entry.AuditId);
    }

    [Fact]
    public async Task ConfirmMode_Deny_IsDenied()
    {
        await using var h = await CreateAsync();
        var approvals = h.Get<IApprovalService>();
        approvals.RequestAdded += (_, req) => approvals.Resolve(req.ApprovalId, false, "Tester");
        var r = await h.Bus.SendAsync(Buy(ConfirmAi));
        Assert.Equal(CommandStatus.Denied, r.Status);
        Assert.Contains("abgelehnt", r.Message);
        Assert.Empty(h.Engine.GetOrders(Acc));
        Assert.Empty(approvals.Pending);
    }

    [Fact]
    public async Task ConfirmMode_Timeout_IsDeniedAsExpired()
    {
        await using var h = await CreateAsync(approvalTimeout: TimeSpan.FromMilliseconds(150));
        var resolved = new List<ApprovalRequest>();
        h.Get<IApprovalService>().RequestResolved += (_, r) => resolved.Add(r);
        var r = await h.Bus.SendAsync(Buy(ConfirmAi));
        Assert.Equal(CommandStatus.Denied, r.Status);
        Assert.Contains("abgelaufen", r.Message);
        Assert.Equal(ApprovalState.Expired, Assert.Single(resolved).State);
        Assert.Empty(h.Engine.GetOrders(Acc));
    }

    [Fact]
    public async Task ConfirmMode_WatchlistNeedsNoApproval()
    {
        await using var h = await CreateAsync(approvalTimeout: TimeSpan.FromMilliseconds(50));
        var r = await h.Bus.SendAsync(new CreateWatchlistCommand { Source = CommandSource.Ai, ClientId = ConfirmAi, Name = "KI-Liste", Symbols = ["btc/eur"] });
        Assert.Equal(CommandStatus.Executed, r.Status);
        Assert.Equal(["BTC/EUR"], ((Watchlist)r.Data!).Symbols);
    }

    [Fact]
    public async Task RateLimit_Ai_PerMinute()
    {
        await using var h = await CreateAsync(risk: RiskConfig.Default with { MaxOrdersPerMinuteAi = 3 });
        for (var i = 0; i < 3; i++)
            Assert.Equal(CommandStatus.Executed, (await h.Bus.SendAsync(Buy(AutoAi))).Status);
        var limited = await h.Bus.SendAsync(Buy(AutoAi));
        Assert.Equal(CommandStatus.Rejected, limited.Status);
        Assert.Contains("Ratenlimit", limited.Message);
        // Mensch ist davon nicht betroffen.
        Assert.Equal(CommandStatus.Executed, (await h.Bus.SendAsync(Buy("ui"))).Status);

        h.Clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(CommandStatus.Executed, (await h.Bus.SendAsync(Buy(AutoAi))).Status);
        Assert.Equal(1, h.Get<ITradingQueries>().GetAgentStatus(AutoAi).OrdersLastMinute);
    }

    [Fact]
    public async Task ClientLimits_AllowedSymbols_And_MaxOpenOrders()
    {
        await using var h = await CreateAsync(risk: RiskConfig.Default with { AllowedSymbols = ["BTC/EUR"], MaxOpenOrders = 2 });
        Assert.Equal(CommandStatus.Rejected, (await h.Bus.SendAsync(Buy(AutoAi, symbol: "ETH/EUR"))).Status);
        Assert.Equal(CommandStatus.Executed, (await h.Bus.SendAsync(Buy(AutoAi))).Status);
        Assert.Equal(CommandStatus.Executed, (await h.Bus.SendAsync(Buy(AutoAi))).Status);
        var third = await h.Bus.SendAsync(Buy(AutoAi));
        Assert.Equal(CommandStatus.Rejected, third.Status);
        Assert.Contains("offene Orders", third.Message);
    }

    [Fact]
    public async Task DailyLossLimit_MakesAiReadOnly_AndCancelsAiOrders()
    {
        await using var h = await CreateAsync();
        var aiOrder = (Order)(await h.Bus.SendAsync(Buy(AutoAi))).Data!;
        var humanOrder = (Order)(await h.Bus.SendAsync(Buy("ui"))).Data!;

        h.Engine.TriggerDailyLoss(Acc);
        await WaitUntilAsync(() => !h.Engine.GetOrder(Acc, aiOrder.OrderId)!.IsOpen);
        Assert.True(h.Engine.GetOrder(Acc, humanOrder.OrderId)!.IsOpen);

        var denied = await h.Bus.SendAsync(Buy(AutoAi));
        Assert.Equal(CommandStatus.Denied, denied.Status);
        Assert.Contains("Tagesverlust", denied.Message);
        Assert.Equal(CommandStatus.Previewed, (await h.Bus.SendAsync(Buy(AutoAi, dryRun: true))).Status);
        Assert.Equal(CommandStatus.Executed, (await h.Bus.SendAsync(Buy("ui"))).Status);

        Assert.Equal(ClientMode.ReadOnly, h.Get<ITradingQueries>().GetAgentStatus(AutoAi).Mode);
        await WaitUntilAsync(async () => (await h.Get<IAuditLog>().QueryAsync(clientId: CommandBus.SystemClientId)).Count == 1);
        Assert.Contains(h.Notifier.Notifications, n => n.Title.Contains("Tagesverlust"));
    }

    [Fact]
    public async Task KillSwitch_AiCanEngage_ButNotRelease()
    {
        await using var h = await CreateAsync();
        await h.Bus.SendAsync(Buy(AutoAi));
        var engage = await h.Bus.SendAsync(new EngageKillSwitchCommand { Source = CommandSource.Ai, ClientId = AutoAi, Reason = "Markt verrückt" });
        Assert.Equal(CommandStatus.Executed, engage.Status);
        Assert.True(h.Engine.IsKillSwitchEngaged(Acc));
        Assert.Empty(h.Engine.GetOrders(Acc, openOnly: true));

        var place = await h.Bus.SendAsync(Buy("ui"));
        Assert.Equal(CommandStatus.Rejected, place.Status);
        Assert.Contains("Not-Aus", place.Message);

        var aiRelease = await h.Bus.SendAsync(new ReleaseKillSwitchCommand { Source = CommandSource.Ai, ClientId = SettingsAi });
        Assert.Equal(CommandStatus.Denied, aiRelease.Status);
        Assert.True(h.Engine.IsKillSwitchEngaged(Acc));

        var release = await h.Bus.SendAsync(new ReleaseKillSwitchCommand());
        Assert.Equal(CommandStatus.Executed, release.Status);
        Assert.False(h.Engine.IsKillSwitchEngaged(Acc));
    }

    [Fact]
    public async Task KillSwitch_DeniesPendingApprovals()
    {
        await using var h = await CreateAsync();
        var approvals = h.Get<IApprovalService>();
        var pending = h.Bus.SendAsync(Buy(ConfirmAi));
        await WaitUntilAsync(() => approvals.Pending.Count == 1);
        await h.Bus.SendAsync(new EngageKillSwitchCommand { Reason = "Stopp" });
        var r = await pending;
        Assert.Equal(CommandStatus.Denied, r.Status);
        Assert.Empty(h.Engine.GetOrders(Acc));
    }

    [Fact]
    public async Task UpdateSettings_AiCannotChangeLimitsOrModes()
    {
        await using var h = await CreateAsync();
        var settings = h.Get<SettingsService>();
        var cur = settings.Current;

        var risk = await h.Bus.SendAsync(new UpdateSettingsCommand { Source = CommandSource.Ai, ClientId = SettingsAi, Settings = cur with { Risk = cur.Risk with { MaxDailyLoss = 1_000_000m } } });
        Assert.Equal(CommandStatus.Denied, risk.Status);

        var mode = await h.Bus.SendAsync(new UpdateSettingsCommand
        {
            Source = CommandSource.Ai, ClientId = SettingsAi,
            Settings = cur with { AiClients = cur.AiClients.Select(c => c with { Mode = ClientMode.Autonomous }).ToList() },
        });
        Assert.Equal(CommandStatus.Denied, mode.Status);
        Assert.Equal(ClientMode.ReadOnly, h.Get<ClientRegistry>().Get(ReadOnlyAi)!.Mode);

        // Strukturell gleiche Limits (neue Listeninstanz nach JSON-Rundlauf) sind erlaubt.
        var roundtrip = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(cur, Core.Serialization.JsonDefaults.Options), Core.Serialization.JsonDefaults.Options)!;
        var ok = await h.Bus.SendAsync(new UpdateSettingsCommand { Source = CommandSource.Ai, ClientId = SettingsAi, Settings = roundtrip with { Theme = "dark" } });
        Assert.Equal(CommandStatus.Executed, ok.Status);
        Assert.Equal("dark", (await h.Get<ISettingsStore>().LoadAsync()).Theme);
    }

    [Fact]
    public async Task UpdateRiskLimits_And_SetClientMode_ByHuman()
    {
        await using var h = await CreateAsync();
        var newRisk = RiskConfig.Default with { MaxOrdersPerMinuteAi = 2 };
        Assert.Equal(CommandStatus.Executed, (await h.Bus.SendAsync(new UpdateRiskLimitsCommand { Risk = newRisk })).Status);
        Assert.Equal(2, h.Engine.GetRiskConfig(Acc).MaxOrdersPerMinuteAi);
        Assert.Equal(2, (await h.Get<ISettingsStore>().LoadAsync()).Risk.MaxOrdersPerMinuteAi);

        var changed = 0;
        h.Get<IClientRegistry>().Changed += (_, _) => changed++;
        var r = await h.Bus.SendAsync(new SetClientModeCommand { TargetClientId = ReadOnlyAi, Mode = ClientMode.Autonomous, Scopes = ClientScopes.Read | ClientScopes.Trade });
        Assert.Equal(CommandStatus.Executed, r.Status);
        Assert.Equal(1, changed);
        Assert.Equal(ClientMode.Autonomous, h.Get<IClientRegistry>().Get(ReadOnlyAi)!.Mode);
        Assert.Equal(CommandStatus.Executed, (await h.Bus.SendAsync(Buy(ReadOnlyAi))).Status);
        Assert.Equal(CommandStatus.Rejected, (await h.Bus.SendAsync(new SetClientModeCommand { TargetClientId = "nope", Mode = ClientMode.Autonomous })).Status);
    }

    [Fact]
    public async Task Ai_CanOnlyTouchOwnOrders()
    {
        await using var h = await CreateAsync();
        var human = (Order)(await h.Bus.SendAsync(Buy("ui"))).Data!;
        var own = (Order)(await h.Bus.SendAsync(Buy(AutoAi))).Data!;

        Assert.Equal(CommandStatus.Denied, (await h.Bus.SendAsync(new CancelOrderCommand { Source = CommandSource.Ai, ClientId = AutoAi, OrderId = human.OrderId })).Status);
        Assert.Equal(CommandStatus.Executed, (await h.Bus.SendAsync(new ModifyOrderCommand { Source = CommandSource.Ai, ClientId = AutoAi, OrderId = own.OrderId, NewLimitPrice = 59_000m })).Status);
        Assert.Equal(59_000m, h.Engine.GetOrder(Acc, own.OrderId)!.LimitPrice);

        await h.Bus.SendAsync(Buy(AutoAi));
        var all = await h.Bus.SendAsync(new CancelAllOrdersCommand { Source = CommandSource.Ai, ClientId = AutoAi });
        Assert.Equal(CommandStatus.Executed, all.Status);
        Assert.Equal(2, ((IReadOnlyList<string>)all.Data!).Count);
        Assert.True(h.Engine.GetOrder(Acc, human.OrderId)!.IsOpen);

        Assert.Equal(CommandStatus.Rejected, (await h.Bus.SendAsync(new CancelOrderCommand { OrderId = "gibt-es-nicht" })).Status);
    }

    [Fact]
    public async Task Audit_EveryCommandIsLoggedCompletely()
    {
        await using var h = await CreateAsync();
        var ok = await h.Bus.SendAsync(Buy(AutoAi, "audit-key"));
        var denied = await h.Bus.SendAsync(Buy(ReadOnlyAi));
        var dup = await h.Bus.SendAsync(Buy(AutoAi, "audit-key"));

        var entries = await h.Get<IAuditLog>().QueryAsync();
        Assert.Equal(3, entries.Count);
        var e = entries.Single(x => x.AuditId == ok.AuditId);
        Assert.Equal(AutoAi, e.ClientId);
        Assert.Equal(CommandSource.Ai, e.Source);
        Assert.Equal("PlaceOrderCommand", e.Action);
        Assert.Equal("Executed", e.Result);
        Assert.Equal("audit-key", e.IdempotencyKey);
        Assert.NotNull(e.Duration);
        Assert.Contains("BTC/EUR", e.ParametersJson);
        Assert.Contains("60000", e.ParametersJson);
        Assert.Null(e.ApprovedBy);
        Assert.Equal("Denied", entries.Single(x => x.AuditId == denied.AuditId).Result);
        Assert.NotNull(entries.Single(x => x.AuditId == denied.AuditId).Message);
        Assert.Equal("Duplicate", entries.Single(x => x.AuditId == dup.AuditId).Result);
    }

    [Fact]
    public async Task WatchlistAndAlertCommands_UseStores()
    {
        await using var h = await CreateAsync();
        var wl = (Watchlist)(await h.Bus.SendAsync(new CreateWatchlistCommand { Name = "Meine", Symbols = ["BTC/EUR"] })).Data!;
        Assert.Equal(CommandStatus.Executed, (await h.Bus.SendAsync(new AddToWatchlistCommand { WatchlistId = wl.WatchlistId, Symbol = "eth/eur" })).Status);
        Assert.Equal(CommandStatus.Executed, (await h.Bus.SendAsync(new RemoveFromWatchlistCommand { WatchlistId = wl.WatchlistId, Symbol = "BTC/EUR" })).Status);
        Assert.Equal(CommandStatus.Rejected, (await h.Bus.SendAsync(new RemoveFromWatchlistCommand { WatchlistId = wl.WatchlistId, Symbol = "XRP/EUR" })).Status);
        Assert.Equal(["ETH/EUR"], (await h.Get<IWatchlistStore>().GetAllAsync()).Single().Symbols);
        Assert.Equal(CommandStatus.Executed, (await h.Bus.SendAsync(new DeleteWatchlistCommand { WatchlistId = wl.WatchlistId })).Status);
        Assert.Empty(await h.Get<IWatchlistStore>().GetAllAsync());

        var alert = (PriceAlert)(await h.Bus.SendAsync(new CreateAlertCommand { Source = CommandSource.Ai, ClientId = AutoAi, Symbol = "BTC/EUR", Condition = AlertCondition.PriceAbove, Threshold = 70_000m })).Data!;
        Assert.Single(await h.Get<IAlertStore>().GetAllAsync());
        Assert.Equal(CommandStatus.Executed, (await h.Bus.SendAsync(new DeleteAlertCommand { AlertId = alert.AlertId })).Status);
        Assert.Equal(CommandStatus.Rejected, (await h.Bus.SendAsync(new DeleteAlertCommand { AlertId = alert.AlertId })).Status);
    }

    [Fact]
    public async Task InvalidOrder_IsRejectedBeforeEngine()
    {
        await using var h = await CreateAsync();
        var r = await h.Bus.SendAsync(Buy("ui") with { Quantity = null });
        Assert.Equal(CommandStatus.Rejected, r.Status);
        Assert.Equal(CommandStatus.Rejected, (await h.Bus.SendAsync(Buy("ui") with { LimitPrice = null })).Status);
        Assert.Empty(h.Engine.Executed.OfType<PlaceOrderRequest>());
    }
}
