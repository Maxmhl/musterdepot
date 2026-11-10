using FsCheck.Xunit;
using Musterdepot.Commands;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Commands;
using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;
using Musterdepot.IntegrationTests.Infrastructure;
using Musterdepot.Recorder.Fixtures;

namespace Musterdepot.IntegrationTests;

/// <summary>
/// Ende-zu-Ende mit allen <c>AddMusterdepot*</c>-Erweiterungen: SQLite im Temp-Verzeichnis, Replay-Quelle auf der
/// Host-Uhr, Engine, Befehlsschicht, MCP (Port 0) und <see cref="IMusterdepotRuntime"/>.
/// </summary>
[Collection(TestHostCollection.Name)]
public class RuntimeEndToEndTests
{
    private const string Acc = "main";
    private static readonly DateTimeOffset QuietStart = new(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);

    private static string QuietReplay => Path.Combine(TestPaths.ReplaysDirectory, FixtureGenerator.QuietName + ".jsonl");

    private static TestHost CreateHost(string dataDir, Action<TestHostOptions>? configure = null) => TestHost.Create(o =>
    {
        o.ReplayFile = QuietReplay;
        o.DataDirectory = dataDir;
        o.StartUtc = QuietStart;
        configure?.Invoke(o);
    });

    private static PlaceOrderCommand Order(string key, OrderSide side, OrderType type, decimal qty, decimal? limit = null, decimal? stop = null,
        CommandSource source = CommandSource.Human, string clientId = "ui") => new()
    {
        Source = source, ClientId = clientId, IdempotencyKey = key, Symbol = "BTC/EUR", Side = side, Type = type,
        Quantity = qty, LimitPrice = limit, StopPrice = stop, TimeInForce = TimeInForce.Gtc,
    };

    private static decimal LastPrice(TestHost host) => host.Get<IMarketDataHub>().GetLatestQuote("BTC/EUR")?.Last
        ?? throw new InvalidOperationException("Noch kein Kurs.");

    /// <summary>Wartet, bis die Laufzeit alle Engine-Events in SQLite geschrieben hat.</summary>
    private static Task WaitPersistedAsync(TestHost host) => TestHost.WaitUntilAsync(async () =>
        (await host.Get<IEventStore>().ReadAllAsync(Acc)).Count == host.Engine.GetEvents(Acc).Count, what: "Events gespeichert");

    [Fact]
    public async Task Runtime_RunsReplay_SuspendResume_Stop_AndRestartRestoresIdenticalState()
    {
        var dir = TestPaths.NewTempDirectory("musterdepot-e2e");
        try
        {
            string fingerprint;
            int events;
            await using (var host = CreateHost(dir))
            {
                var runtime = host.Runtime;
                await runtime.StartAsync();
                Assert.True(runtime.IsRunning);
                Assert.True(host.Engine.AccountExists(Acc), "Standardkonto fehlt");
                var replay = host.Replay!;

                replay.AdvanceTo(QuietStart.AddMinutes(2));
                var buy = await host.Bus.SendAsync(Order("e2e-buy", OrderSide.Buy, OrderType.Market, 0.05m));
                Assert.Equal(CommandStatus.Executed, buy.Status);
                replay.AdvanceTo(QuietStart.AddMinutes(3));
                var limit = await host.Bus.SendAsync(Order("e2e-limit", OrderSide.Buy, OrderType.Limit, 0.02m, limit: LastPrice(host) - 300m));
                Assert.Equal(CommandStatus.Executed, limit.Status);
                Assert.Equal(OrderStatus.Filled, host.Engine.GetOrderByClientOrderId(Acc, "e2e-buy")!.Status);

                // Standby: Quelle pausiert, Uhr läuft weiter, keine Ticks an die Engine.
                await runtime.SuspendAsync();
                Assert.Equal(ConnectionState.Suspended, host.Get<IMarketDataHub>().GetConnectionStates()["replay"]);
                host.Clock.Advance(TimeSpan.FromMinutes(5));
                await runtime.ResumeAsync();
                Assert.Equal(ConnectionState.Connected, host.Get<IMarketDataHub>().GetConnectionStates()["replay"]);

                replay.AdvanceTo(QuietStart.AddMinutes(20));
                var stop = await host.Bus.SendAsync(Order("e2e-stop", OrderSide.Sell, OrderType.Stop, 0.05m, stop: LastPrice(host) - 500m));
                Assert.Equal(CommandStatus.Executed, stop.Status);
                replay.RunToEnd();
                Invariants.CheckAccount(host.Engine, Acc);

                await WaitPersistedAsync(host);
                await runtime.StopAsync();
                Assert.False(runtime.IsRunning);
                fingerprint = Invariants.StateFingerprint(host.Engine);
                events = host.Engine.GetEvents(Acc).Count;
                Assert.True(events > 5);
            }

            // Neustart mit neuem ServiceProvider auf derselben Datenbank.
            await using (var host = CreateHost(dir))
            {
                Assert.False(host.Engine.AccountExists(Acc));
                await host.Runtime.StartAsync();
                Assert.Equal(events, host.Engine.GetEvents(Acc).Count);
                Assert.Equal(fingerprint, Invariants.StateFingerprint(host.Engine));

                // Idempotenz überlebt den Neustart (ClientOrderId in der Engine).
                var again = await host.Bus.SendAsync(Order("e2e-buy", OrderSide.Buy, OrderType.Market, 0.05m));
                Assert.Equal(CommandStatus.Duplicate, again.Status);
                // Neue Events setzen die Sequenz fort.
                var cancel = await host.Bus.SendAsync(new CancelOrderCommand { IdempotencyKey = "c1", OrderId = host.Engine.GetOrderByClientOrderId(Acc, "e2e-stop")!.OrderId });
                Assert.Equal(CommandStatus.Executed, cancel.Status);
                var seq = host.Engine.GetEvents(Acc).Select(e => e.Sequence).ToList();
                Assert.Equal(Enumerable.Range(1, seq.Count).Select(i => (long)i), seq);
                await WaitPersistedAsync(host);
                await host.Runtime.StopAsync();
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TestPaths.TryDeleteDirectory(dir);
        }
    }

    /// <summary>LoadFromEvents ≡ Live über einen echten SQLite-Neustart, mit zufälligen Orders während des Replays.</summary>
    [Property(MaxTest = 5)]
    public void Restart_from_sqlite_equals_live_state(int seed)
    {
        Task.Run(async () =>
        {
            var dir = TestPaths.NewTempDirectory("musterdepot-restart");
            var r = new Random(seed);
            try
            {
                string live;
                await using (var host = CreateHost(dir))
                {
                    await host.Runtime.StartAsync();
                    var t = QuietStart.AddMinutes(1);
                    for (int i = 0; i < 12; i++)
                    {
                        t = t.AddSeconds(r.Next(30, 140));
                        host.Replay!.AdvanceTo(t);
                        var price = LastPrice(host);
                        var side = r.Next(3) == 0 ? OrderSide.Sell : OrderSide.Buy;
                        var qty = Math.Round(0.005m + (decimal)r.NextDouble() * 0.05m, 4);
                        ICommand cmd = r.Next(5) switch
                        {
                            0 => Order($"m{i}", side, OrderType.Market, qty),
                            1 or 2 => Order($"l{i}", side, OrderType.Limit, qty, limit: Math.Round(price + (side == OrderSide.Buy ? -1 : 1) * r.Next(0, 30), 1)),
                            3 => Order($"s{i}", side, OrderType.Stop, qty, stop: Math.Round(price + (side == OrderSide.Buy ? 1 : -1) * r.Next(5, 40), 1)),
                            _ => host.Engine.GetOrders(Acc, openOnly: true) is { Count: > 0 } open
                                ? new CancelOrderCommand { IdempotencyKey = $"c{i}", OrderId = open[r.Next(open.Count)].OrderId }
                                : Order($"x{i}", OrderSide.Buy, OrderType.Market, qty),
                        };
                        await host.Bus.SendAsync(cmd);
                    }
                    host.Replay!.RunToEnd();
                    Invariants.CheckAccount(host.Engine, Acc);
                    await WaitPersistedAsync(host);
                    await host.Runtime.StopAsync();
                    live = Invariants.StateFingerprint(host.Engine);
                }
                await using (var host = CreateHost(dir))
                {
                    await host.Runtime.StartAsync();
                    Assert.Equal(live, Invariants.StateFingerprint(host.Engine));
                    await host.Runtime.StopAsync();
                }
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                TestPaths.TryDeleteDirectory(dir);
            }
        }).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task ConfirmMode_ApproveDenyAndTimeout()
    {
        var dir = TestPaths.NewTempDirectory("musterdepot-confirm");
        try
        {
            await using var host = CreateHost(dir, o => o.Commands.ApprovalTimeout = TimeSpan.FromSeconds(1));
            await host.Runtime.StartAsync();
            host.Replay!.AdvanceTo(QuietStart.AddMinutes(5));
            var (client, _) = await host.Get<ITokenService>().CreateClientAsync("Confirm-KI", ClientScopes.Read | ClientScopes.Trade, ClientMode.Confirm);
            var approvals = host.Get<IApprovalService>();
            var price = LastPrice(host);

            // 1) Mensch erlaubt über IApprovalService.Resolve.
            var pending = host.Bus.SendAsync(Order("ai-1", OrderSide.Buy, OrderType.Limit, 0.01m, limit: price - 50m, source: CommandSource.Ai, clientId: client.ClientId));
            await TestHost.WaitUntilAsync(() => approvals.Pending.Count == 1, what: "Freigabe 1 angefragt");
            Assert.False(pending.IsCompleted);
            Assert.Null(host.Engine.GetOrders(Acc).FirstOrDefault(o => o.ClientOrderId.EndsWith("ai-1", StringComparison.Ordinal)));
            Assert.True(approvals.Resolve(approvals.Pending[0].ApprovalId, approved: true, decidedBy: "Tester"));
            var approved = await pending;
            Assert.Equal(CommandStatus.Executed, approved.Status);
            Assert.NotNull(approved.ApprovalId);
            Assert.Single(host.Engine.GetOrders(Acc), o => o.Source == CommandSource.Ai && o.ClientName == "Confirm-KI");

            // 2) Mensch lehnt über ResolveApprovalCommand ab.
            pending = host.Bus.SendAsync(Order("ai-2", OrderSide.Buy, OrderType.Limit, 0.01m, limit: price - 60m, source: CommandSource.Ai, clientId: client.ClientId));
            await TestHost.WaitUntilAsync(() => approvals.Pending.Count == 1, what: "Freigabe 2 angefragt");
            var deny = await host.Bus.SendAsync(new ResolveApprovalCommand { IdempotencyKey = "deny-2", ApprovalId = approvals.Pending[0].ApprovalId, Approved = false });
            Assert.Equal(CommandStatus.Executed, deny.Status);
            var denied = await pending;
            Assert.Equal(CommandStatus.Denied, denied.Status);
            Assert.Contains("abgelehnt", denied.Message);

            // Die KI darf nicht selbst freigeben.
            pending = host.Bus.SendAsync(Order("ai-3", OrderSide.Buy, OrderType.Limit, 0.01m, limit: price - 70m, source: CommandSource.Ai, clientId: client.ClientId));
            await TestHost.WaitUntilAsync(() => approvals.Pending.Count == 1, what: "Freigabe 3 angefragt");
            var self = await host.Bus.SendAsync(new ResolveApprovalCommand { Source = CommandSource.Ai, ClientId = client.ClientId, IdempotencyKey = "self", ApprovalId = approvals.Pending[0].ApprovalId, Approved = true });
            Assert.Equal(CommandStatus.Denied, self.Status);

            // 3) Niemand entscheidet → Timeout → Denied.
            var expired = await pending;
            Assert.Equal(CommandStatus.Denied, expired.Status);
            Assert.Contains("abgelaufen", expired.Message);
            Assert.Empty(approvals.Pending);
            Assert.Single(host.Engine.GetOrders(Acc), o => o.Source == CommandSource.Ai);

            // Protokoll enthält Freigebenden.
            var audit = await host.Get<IAuditLog>().QueryAsync(clientId: client.ClientId);
            Assert.Contains(audit, a => a.ApprovedBy == "Tester");
            await host.Runtime.StopAsync();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TestPaths.TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task KillSwitch_AiEngages_AiCannotRelease_HumanReleases()
    {
        var dir = TestPaths.NewTempDirectory("musterdepot-kill");
        try
        {
            await using var host = CreateHost(dir);
            await host.Runtime.StartAsync();
            host.Replay!.AdvanceTo(QuietStart.AddMinutes(5));
            var (client, _) = await host.Get<ITokenService>().CreateClientAsync("Auto-KI", ClientScopes.Read | ClientScopes.Trade, ClientMode.Autonomous);
            var price = LastPrice(host);
            var open = await host.Bus.SendAsync(Order("ai-open", OrderSide.Buy, OrderType.Limit, 0.01m, limit: price - 100m, source: CommandSource.Ai, clientId: client.ClientId));
            Assert.Equal(CommandStatus.Executed, open.Status);

            var engage = await host.Bus.SendAsync(new EngageKillSwitchCommand { Source = CommandSource.Ai, ClientId = client.ClientId, IdempotencyKey = "kill", Reason = "Test" });
            Assert.Equal(CommandStatus.Executed, engage.Status);
            Assert.True(host.Engine.IsKillSwitchEngaged(Acc));

            Assert.Equal(CommandStatus.Rejected, (await host.Bus.SendAsync(Order("ai-after", OrderSide.Buy, OrderType.Market, 0.01m, source: CommandSource.Ai, clientId: client.ClientId))).Status);
            Assert.Equal(CommandStatus.Rejected, (await host.Bus.SendAsync(Order("ui-after", OrderSide.Buy, OrderType.Market, 0.01m))).Status);

            var aiRelease = await host.Bus.SendAsync(new ReleaseKillSwitchCommand { Source = CommandSource.Ai, ClientId = client.ClientId, IdempotencyKey = "rel-ai" });
            Assert.Equal(CommandStatus.Denied, aiRelease.Status);
            Assert.True(host.Engine.IsKillSwitchEngaged(Acc));
            // Auch ein KI-Befehl, der sich als Mensch ausgibt, wird nicht akzeptiert.
            var spoof = await host.Bus.SendAsync(new ReleaseKillSwitchCommand { Source = CommandSource.Human, ClientId = client.ClientId, IdempotencyKey = "rel-spoof" });
            Assert.Equal(CommandStatus.Denied, spoof.Status);
            Assert.True(host.Engine.IsKillSwitchEngaged(Acc));

            var release = await host.Bus.SendAsync(new ReleaseKillSwitchCommand { IdempotencyKey = "rel-ui" });
            Assert.Equal(CommandStatus.Executed, release.Status);
            Assert.False(host.Engine.IsKillSwitchEngaged(Acc));
            Assert.Equal(CommandStatus.Executed, (await host.Bus.SendAsync(Order("ui-ok", OrderSide.Buy, OrderType.Market, 0.01m))).Status);
            await host.Runtime.StopAsync();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TestPaths.TryDeleteDirectory(dir);
        }
    }
}
