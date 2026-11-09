using Microsoft.Extensions.DependencyInjection;
using Musterdepot.Commands.Tests.Fakes;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Commands;
using Musterdepot.Core.Domain;
using Musterdepot.Core.Events;
using Musterdepot.Core.MarketData;
using Musterdepot.Core.ReadModels;
using static Musterdepot.Commands.Tests.TestHost;

namespace Musterdepot.Commands.Tests;

public sealed class RuntimeAndReadModelTests
{
    [Fact]
    public async Task DependencyInjection_ResolvesAllPublicServices()
    {
        await using var h = await CreateAsync();
        Assert.IsType<CommandBus>(h.Get<ICommandBus>());
        Assert.IsType<TradingQueries>(h.Get<ITradingQueries>());
        Assert.IsType<ReadModels>(h.Get<IReadModels>());
        Assert.IsType<ApprovalQueue>(h.Get<IApprovalService>());
        Assert.IsType<ClientRegistry>(h.Get<IClientRegistry>());
        Assert.IsType<MusterdepotRuntime>(h.Get<IMusterdepotRuntime>());
        Assert.NotNull(h.Get<AlertMonitor>());
        Assert.Same(h.Get<ICommandBus>(), h.Get<CommandBus>());

        // Ohne eigenen Notifier/Uhr greifen die Standardwerte.
        var services = new ServiceCollection().AddMusterdepotCommands();
        await using var sp = services.BuildServiceProvider();
        Assert.IsType<NullUserNotifier>(sp.GetRequiredService<IUserNotifier>());
        Assert.Same(SystemClock.Instance, sp.GetRequiredService<IClock>());
        Assert.Equal(TimeSpan.FromMinutes(2), sp.GetRequiredService<CommandOptions>().ApprovalTimeout);
    }

    [Fact]
    public async Task ReadModels_FollowEngineAuditApprovalsSettingsAndMarket()
    {
        await using var h = await CreateAsync();
        var rm = h.Get<ReadModels>();
        await rm.InitializeAsync();
        Assert.Equal(100_000m, rm.Portfolio.Current.Cash);

        var changed = 0;
        rm.OpenOrders.Changed += (_, _) => Interlocked.Increment(ref changed);
        var order = (Order)(await h.Bus.SendAsync(Buy("ui"))).Data!;
        await WaitUntilAsync(() => rm.OpenOrders.Current.Count == 1 && rm.AllOrders.Current.Count == 1);
        Assert.True(changed >= 1);
        Assert.Equal(order.OrderId, rm.OpenOrders.Current[0].OrderId);
        Assert.Single(rm.AuditLog.Current);

        h.Engine.FillOrder(Acc, order.OrderId, 60_000m);
        await WaitUntilAsync(() => rm.Fills.Current.Count == 1 && rm.OpenOrders.Current.Count == 0);
        Assert.NotEmpty(rm.EquityCurve.Current);

        // Freigaben
        var pending = h.Bus.SendAsync(Buy(ConfirmAi));
        await WaitUntilAsync(() => rm.PendingApprovals.Current.Count == 1);
        await h.Bus.SendAsync(new ResolveApprovalCommand { ApprovalId = rm.PendingApprovals.Current[0].ApprovalId, Approved = false });
        await pending;
        await WaitUntilAsync(() => rm.PendingApprovals.Current.Count == 0);

        // Einstellungen
        var s = h.Get<SettingsService>().Current;
        await h.Bus.SendAsync(new UpdateSettingsCommand { Settings = s with { Theme = "dark" } });
        Assert.Equal("dark", rm.Settings.Current.Theme);

        // Watchlists und Alarme
        await h.Bus.SendAsync(new CreateWatchlistCommand { Name = "W", Symbols = ["BTC/EUR"] });
        await h.Bus.SendAsync(new CreateAlertCommand { Symbol = "BTC/EUR", Condition = AlertCondition.PriceBelow, Threshold = 1m });
        await WaitUntilAsync(() => rm.Watchlists.Current.Count == 1 && rm.Alerts.Current.Count == 1);

        // Kurse (gedrosselt) und Tick-Ereignisse
        var ticks = 0;
        rm.MarketEventReceived += (_, _) => Interlocked.Increment(ref ticks);
        for (var i = 0; i < 20; i++) h.Hub.Publish(FakeMarketDataHub.Quote("BTC/EUR", 60_000m + i));
        Assert.Equal(20, ticks);
        await WaitUntilAsync(() => rm.Quotes.Current.TryGetValue("BTC/EUR", out var q) && q.Last == 60_019m);

        h.Hub.Publish(new ConnectionStatusChanged { Symbol = "*", Source = "kraken", AsOfUtc = DateTimeOffset.UtcNow, State = ConnectionState.Connected });
        Assert.Equal(ConnectionState.Connected, rm.Connections.Current["kraken"]);

        // Not-Aus
        await h.Bus.SendAsync(new EngageKillSwitchCommand { Reason = "Test" });
        await WaitUntilAsync(() => rm.KillSwitch.Current);
    }

    [Fact]
    public async Task ReadModels_QuoteUpdatesAreThrottled()
    {
        await using var h = await CreateAsync();
        var rm = h.Get<ReadModels>();
        await rm.InitializeAsync();
        var updates = 0;
        rm.Quotes.Changed += (_, _) => Interlocked.Increment(ref updates);
        for (var i = 0; i < 200; i++) h.Hub.Publish(FakeMarketDataHub.Quote("ETH/EUR", 3000m + i));
        await WaitUntilAsync(() => Volatile.Read(ref updates) >= 1);
        Assert.InRange(updates, 1, 20); // 50 ms Takt im Test → wenige Updates statt 200
        Assert.Equal(3199m, rm.Quotes.Current["ETH/EUR"].Last);
    }

    [Fact]
    public async Task AlertMonitor_TriggersOnce_AndNotifies()
    {
        await using var h = await CreateAsync();
        var monitor = h.Get<AlertMonitor>();
        await monitor.StartAsync();
        var created = await h.Bus.SendAsync(new CreateAlertCommand { Symbol = "BTC/EUR", Condition = AlertCondition.PriceAbove, Threshold = 70_000m, Note = "Ziel" });
        await monitor.ReloadAsync();

        h.Hub.Publish(FakeMarketDataHub.Quote("BTC/EUR", 69_000m));
        await Task.Delay(50);
        Assert.True((await h.Get<IAlertStore>().GetAllAsync()).Single().IsActive);

        h.Hub.Publish(new TradeTick { Symbol = "BTC/EUR", Source = "fake", AsOfUtc = DateTimeOffset.UtcNow, Price = 70_500m, Quantity = 1 });
        await WaitUntilAsync(async () => !(await h.Get<IAlertStore>().GetAllAsync()).Single().IsActive);
        h.Hub.Publish(FakeMarketDataHub.Quote("BTC/EUR", 71_000m));
        await Task.Delay(50);
        Assert.Single(h.Notifier.Notifications, n => n.Title.Contains("Preisalarm"));
        Assert.Equal(((PriceAlert)created.Data!).AlertId, (await h.Get<IAlertStore>().GetAllAsync()).Single().AlertId);

        // Änderung in Prozent
        await h.Bus.SendAsync(new CreateAlertCommand { Symbol = "ETH/EUR", Condition = AlertCondition.ChangePercentBelow, Threshold = -5m });
        await monitor.ReloadAsync();
        var hits = await monitor.CheckAsync("ETH/EUR", 2000m, -6m);
        Assert.Single(hits);
        monitor.Stop();
    }

    [Fact]
    public async Task Queries_CandlesFromHubOrStore_WithLimit_AndAgentStatus()
    {
        await using var h = await CreateAsync();
        var q = h.Get<ITradingQueries>();
        var t0 = new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);
        Candle C(int i) => new() { Symbol = "BTC/EUR", Timeframe = Timeframe.M1, OpenTimeUtc = t0.AddMinutes(i), Open = i, High = i, Low = i, Close = i };

        await h.Get<ICandleStore>().UpsertAsync(Enumerable.Range(0, 10).Select(C).ToList());
        var fromStore = await q.GetCandlesAsync("BTC/EUR", Timeframe.M1, null, null, 3);
        Assert.Equal([7m, 8m, 9m], fromStore.Select(c => c.Close));

        h.Hub.Candles.AddRange(Enumerable.Range(100, 5).Select(C));
        var fromHub = await q.GetCandlesAsync("BTC/EUR", Timeframe.M1, null, null, 2);
        Assert.Equal([103m, 104m], fromHub.Select(c => c.Close));

        h.Hub.Publish(FakeMarketDataHub.Quote("BTC/EUR", 1m));
        Assert.Equal(1m, q.GetQuote("BTC/EUR")!.Last);
        Assert.Equal(100_000m, q.GetPortfolio().Cash);

        await h.Bus.SendAsync(Buy(AutoAi));
        var status = q.GetAgentStatus(AutoAi);
        Assert.Equal("Auto-KI", status.ClientName);
        Assert.Equal(ClientMode.Autonomous, status.Mode);
        Assert.Equal(1, status.OrdersLastMinute);
        Assert.Equal(10, status.MaxOrdersPerMinute);
        Assert.NotNull(status.LastActivityUtc);

        var unknown = q.GetAgentStatus("niemand");
        Assert.Equal(ClientMode.ReadOnly, unknown.Mode);
        Assert.Equal(ClientScopes.None, unknown.Scopes);

        await h.Get<IInstrumentStore>().UpsertAsync([new Instrument { Symbol = "DE0007164600", Name = "SAP SE", AssetClass = AssetClass.Stock, QuoteCurrency = "EUR", Isin = "DE0007164600" }]);
        Assert.Single(await q.SearchSymbolsAsync("sap"));
        Assert.Single(await q.GetAuditLogAsync(null, AutoAi, 10));
    }

    [Fact]
    public async Task Runtime_Start_CreatesDefaultAccount_AndPersistsEvents()
    {
        await using var h = await CreateAsync(openAccount: false);
        var runtime = h.Get<IMusterdepotRuntime>();
        await runtime.StartAsync();
        Assert.True(runtime.IsRunning);
        Assert.True(h.Engine.AccountExists(Acc));
        Assert.True(h.Hub.Started);

        var store = h.Get<IEventStore>();
        await WaitUntilAsync(async () => (await store.ReadAllAsync()).OfType<AccountOpened>().Any());
        var opened = (await store.ReadAllAsync()).OfType<AccountOpened>().Single();
        Assert.Equal(100_000m, opened.InitialCash);

        // Befehle → Engine-Events → Event-Store (geordnet)
        await h.Bus.SendAsync(Buy("ui"));
        await WaitUntilAsync(async () => (await store.ReadAllAsync()).Count == 3);
        Assert.Equal([1L, 2L, 3L], (await store.ReadAllAsync()).Select(e => e.Sequence));

        // Uhr-Takt und Marktdaten an die Engine
        await WaitUntilAsync(() => h.Engine.Ticks.Count > 0);
        h.Hub.Publish(FakeMarketDataHub.Quote("BTC/EUR", 1m));
        Assert.Single(h.Engine.MarketEvents);

        // Abgeschlossene Kerzen → Kerzenspeicher
        var candle = new Candle { Symbol = "BTC/EUR", Timeframe = Timeframe.M1, OpenTimeUtc = h.Clock.UtcNow.AddMinutes(-1), Open = 1, High = 2, Low = 1, Close = 2, IsClosed = true };
        h.Hub.Publish(new CandleUpdate { Symbol = "BTC/EUR", Source = "fake", AsOfUtc = h.Clock.UtcNow, Candle = candle });
        h.Hub.Publish(new CandleUpdate { Symbol = "BTC/EUR", Source = "fake", AsOfUtc = h.Clock.UtcNow, Candle = candle with { OpenTimeUtc = h.Clock.UtcNow, IsClosed = false } });
        await WaitUntilAsync(async () => (await h.Get<ICandleStore>().GetAsync("BTC/EUR", Timeframe.M1, null, null)).Count == 1);

        // Abos: offene Order und neue Watchlist
        Assert.Contains("BTC/EUR", h.Hub.Subscribed);
        await h.Bus.SendAsync(new CreateWatchlistCommand { Name = "W", Symbols = ["SOL/EUR"] });
        await WaitUntilAsync(() => h.Hub.Subscribed.Contains("SOL/EUR"));

        await runtime.SuspendAsync();
        Assert.True(h.Hub.Suspended);
        await runtime.ResumeAsync();
        Assert.True(h.Hub.Resumed);

        await runtime.StopAsync();
        Assert.False(runtime.IsRunning);
        Assert.True(h.Hub.Stopped);
    }

    [Fact]
    public async Task Runtime_Start_LoadsStoredEvents_AndSubscribesSymbols()
    {
        using var db = new TempDb();
        // Erster Lauf: Konto, Order, Watchlist anlegen und speichern.
        await using (var first = await CreateAsync(openAccount: false, db: db))
        {
            var rt = first.Get<IMusterdepotRuntime>();
            await rt.StartAsync();
            await first.Bus.SendAsync(Buy("ui", symbol: "ETH/EUR"));
            await first.Bus.SendAsync(new CreateWatchlistCommand { Name = "W", Symbols = ["ADA/EUR"] });
            await WaitUntilAsync(async () => (await first.Get<IEventStore>().ReadAllAsync()).Count == 3);
            await rt.StopAsync();
        }

        await using var second = await CreateAsync(openAccount: false, db: db);
        await second.Get<IMusterdepotRuntime>().StartAsync();
        Assert.Equal(3, second.Engine.LoadedEventCount);
        Assert.True(second.Engine.AccountExists(Acc));
        Assert.Single(second.Engine.GetOrders(Acc, openOnly: true));
        // Kein zweites AccountOpened.
        Assert.DoesNotContain(second.Engine.Executed, c => c is OpenAccountRequest);
        Assert.Contains("ETH/EUR", second.Hub.Subscribed);
        Assert.Contains("ADA/EUR", second.Hub.Subscribed);
        await second.Get<IMusterdepotRuntime>().DisposeAsync();
    }
}
