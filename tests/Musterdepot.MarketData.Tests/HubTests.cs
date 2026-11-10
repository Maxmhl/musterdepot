using Microsoft.Extensions.DependencyInjection;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;
using Musterdepot.MarketData.Bitvavo;
using Musterdepot.MarketData.Fake;
using Musterdepot.MarketData.Fx;
using Musterdepot.MarketData.Gettex;
using Musterdepot.MarketData.Hub;
using Musterdepot.MarketData.Infrastructure;
using Musterdepot.MarketData.Kraken;

namespace Musterdepot.MarketData.Tests;

public class HubTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);
    private static bool Crypto(string s) => SymbolHelper.IsCryptoPair(s);

    private static (MarketDataHub Hub, TestSource Kraken, TestSource Bitvavo, TestSource Coinbase, TestSource Gettex, ManualClock Clock, EventLog Log) Create(TimeSpan? grace = null)
    {
        var clock = new ManualClock(T0);
        // absichtlich in "falscher" Reihenfolge registriert – Priorität kommt aus den Optionen
        var coinbase = new TestSource("coinbase", Crypto);
        var gettex = new TestSource("gettex", SymbolHelper.IsIsin, AssetClass.Stock) { DelayedMinutes = 15 };
        var bitvavo = new TestSource("bitvavo", s => Crypto(s) && s.EndsWith("/EUR"));
        var kraken = new TestSource("kraken", Crypto);
        var hub = new MarketDataHub([coinbase, gettex, bitvavo, kraken], clock, new MarketDataHubOptions
        {
            MaintenanceInterval = TimeSpan.Zero,
            FailoverGracePeriod = grace ?? TimeSpan.FromSeconds(10),
            OpenCandleUpdateInterval = TimeSpan.Zero,
        });
        var log = new EventLog();
        hub.EventReceived += log.Add;
        return (hub, kraken, bitvavo, coinbase, gettex, clock, log);
    }

    [Fact]
    public async Task Routing_CryptoToKraken_StocksToGettex()
    {
        var (hub, kraken, bitvavo, coinbase, gettex, _, _) = Create();
        await hub.StartAsync(default);
        await hub.SubscribeAsync(["BTC/EUR", "DE0007164600", "ETH/USD"], default);
        Assert.Contains("BTC/EUR", kraken.Subscribed);
        Assert.Contains("ETH/USD", kraken.Subscribed);
        Assert.Contains("DE0007164600", gettex.Subscribed);
        Assert.Empty(bitvavo.Subscribed);
        Assert.Empty(coinbase.Subscribed);
        Assert.Equal("kraken", hub.GetActiveSource("BTC/EUR"));
        Assert.Equal("gettex", hub.GetActiveSource("DE0007164600"));
        Assert.Equal(4, hub.GetConnectionStates().Count);
    }

    [Fact]
    public async Task Failover_AfterGrace_AndFailbackWhenPreferredReturns()
    {
        var (hub, kraken, bitvavo, coinbase, _, clock, log) = Create();
        await hub.StartAsync(default);
        await hub.SubscribeAsync(["BTC/EUR", "ETH/USD"], default);

        kraken.SetState(ConnectionState.Reconnecting, clock.UtcNow);
        await hub.RebalanceAsync(default);
        Assert.Equal("kraken", hub.GetActiveSource("BTC/EUR")); // Karenz läuft noch

        clock.Advance(TimeSpan.FromSeconds(11));
        await hub.RunMaintenanceAsync();
        Assert.Equal("bitvavo", hub.GetActiveSource("BTC/EUR"));
        Assert.Equal("coinbase", hub.GetActiveSource("ETH/USD")); // Bitvavo hat nur EUR-Paare
        Assert.Contains("BTC/EUR", bitvavo.Subscribed);
        Assert.DoesNotContain("BTC/EUR", kraken.Subscribed);
        Assert.Contains(log.Of<ConnectionStatusChanged>(), s => s.Symbol == "BTC/EUR" && s.Message!.Contains("kraken → bitvavo"));

        // Ereignisse der inaktiven Quelle werden verworfen
        kraken.Emit(new TradeTick { Symbol = "BTC/EUR", Source = "kraken", AsOfUtc = clock.UtcNow, Price = 1m, Quantity = 1m });
        bitvavo.Emit(new TradeTick { Symbol = "BTC/EUR", Source = "bitvavo", AsOfUtc = clock.UtcNow, Price = 62000m, Quantity = 1m });
        Assert.Equal("bitvavo", Assert.Single(log.Of<TradeTick>()).Source);

        kraken.SetState(ConnectionState.Connected, clock.UtcNow);
        await hub.RebalanceAsync(default);
        Assert.Equal("kraken", hub.GetActiveSource("BTC/EUR"));
        Assert.Contains("BTC/EUR", kraken.Subscribed);
        Assert.DoesNotContain("BTC/EUR", bitvavo.Subscribed);
        Assert.Equal("kraken", hub.GetActiveSource("ETH/USD"));
    }

    [Fact]
    public async Task Failover_Immediate_WhenSourceFailed()
    {
        var (hub, kraken, _, _, _, clock, _) = Create();
        await hub.StartAsync(default);
        await hub.SubscribeAsync(["BTC/EUR"], default);
        kraken.SetState(ConnectionState.Failed, clock.UtcNow);
        await hub.RebalanceAsync(default);
        Assert.Equal("bitvavo", hub.GetActiveSource("BTC/EUR"));
    }

    [Fact]
    public async Task QuoteCache_MergesQuotesAndTrades_WithProvenance()
    {
        var (hub, kraken, _, _, gettex, clock, _) = Create();
        await hub.StartAsync(default);
        await hub.SubscribeAsync(["BTC/EUR", "DE0007164600"], default);
        kraken.Emit(new QuoteTick { Symbol = "BTC/EUR", Source = "kraken", AsOfUtc = T0, Bid = 100m, Ask = 101m, Volume24h = 5m });
        kraken.Emit(new TradeTick { Symbol = "BTC/EUR", Source = "kraken", AsOfUtc = T0.AddSeconds(1), Price = 100.5m, Quantity = 1m });
        var q = hub.GetLatestQuote("BTC/EUR")!;
        Assert.Equal((100m, 101m, 100.5m), (q.Bid!.Value, q.Ask!.Value, q.Last!.Value));
        Assert.Equal(100.5m, q.Mid);
        Assert.Equal("kraken", q.Source);
        Assert.True(q.IsRealtime);
        Assert.Equal(0, q.DelayedMinutes);
        Assert.Equal(T0.AddSeconds(1), q.AsOfUtc);
        Assert.Equal("EUR", q.Currency);

        gettex.Emit(new TradeTick { Symbol = "DE0007164600", Source = "gettex", AsOfUtc = T0.AddMinutes(-15), DelayedMinutes = 15, Quality = DataQuality.Delayed, Price = 182.4m, Quantity = 10m });
        var s = hub.GetLatestQuote("DE0007164600")!;
        Assert.False(s.IsRealtime);
        Assert.Equal(15, s.DelayedMinutes);
        Assert.Equal(DataQuality.Delayed, s.Quality);
        Assert.Equal(2, hub.GetAllQuotes().Count);
    }

    [Fact]
    public async Task OrderBook_MaintainedFromSnapshotAndDeltas()
    {
        var (hub, kraken, _, _, _, _, _) = Create();
        await hub.StartAsync(default);
        await hub.SubscribeAsync(["BTC/EUR"], default);
        kraken.Emit(new OrderBookUpdate { Symbol = "BTC/EUR", Source = "kraken", AsOfUtc = T0, IsSnapshot = false, Bids = [new(1m, 1m)], Asks = [] });
        Assert.Null(hub.GetLatestOrderBook("BTC/EUR")); // Delta ohne Snapshot
        kraken.Emit(new OrderBookUpdate { Symbol = "BTC/EUR", Source = "kraken", AsOfUtc = T0, IsSnapshot = true, Bids = [new(100m, 1m), new(99m, 2m)], Asks = [new(101m, 1m)] });
        kraken.Emit(new OrderBookUpdate { Symbol = "BTC/EUR", Source = "kraken", AsOfUtc = T0.AddSeconds(1), IsSnapshot = false, Bids = [new(100m, 0m)], Asks = [new(100.5m, 3m)] });
        var ob = hub.GetLatestOrderBook("BTC/EUR")!;
        Assert.Equal(99m, ob.BestBid);
        Assert.Equal(100.5m, ob.BestAsk);
        Assert.Equal(T0.AddSeconds(1), ob.AsOfUtc);
        Assert.Equal(99m, (await hub.GetOrderBookAsync("BTC/EUR", 1, default))!.BestBid);
    }

    [Fact]
    public async Task Candles_AggregatedFromTrades_WithClosedEvents()
    {
        var (hub, kraken, _, _, _, clock, log) = Create();
        await hub.StartAsync(default);
        await hub.SubscribeAsync(["BTC/EUR"], default);
        kraken.Emit(new TradeTick { Symbol = "BTC/EUR", Source = "kraken", AsOfUtc = T0.AddSeconds(10), Price = 100m, Quantity = 1m });
        kraken.Emit(new TradeTick { Symbol = "BTC/EUR", Source = "kraken", AsOfUtc = T0.AddSeconds(20), Price = 102m, Quantity = 1m });
        kraken.Emit(new TradeTick { Symbol = "BTC/EUR", Source = "kraken", AsOfUtc = T0.AddSeconds(70), Price = 101m, Quantity = 1m });
        var closed = log.Of<CandleUpdate>().Where(c => c.Candle.IsClosed).ToList();
        var m1 = Assert.Single(closed).Candle;
        Assert.Equal((Timeframe.M1, 100m, 102m, 100m, 102m, 2m), (m1.Timeframe, m1.Open, m1.High, m1.Low, m1.Close, m1.Volume));
        Assert.Contains(log.Of<CandleUpdate>(), c => c.Candle.Timeframe == Timeframe.H1 && !c.Candle.IsClosed);
        Assert.Equal(101m, hub.GetCurrentCandle("BTC/EUR", Timeframe.M5)!.Close);

        // Quellen-Kerzen werden standardmäßig nicht weitergereicht (keine Doppelungen)
        kraken.Emit(new CandleUpdate { Symbol = "BTC/EUR", Source = "kraken", AsOfUtc = T0, Candle = m1 with { Source = "kraken" } });
        Assert.Single(log.Of<CandleUpdate>().Where(c => c.Candle.IsClosed));

        // Zeitablauf schließt die Minute 10:01
        clock.Set(T0.AddSeconds(123));
        await hub.RunMaintenanceAsync();
        Assert.Contains(log.Of<CandleUpdate>(), c => c.Candle.IsClosed && c.Candle.Timeframe == Timeframe.M1 && c.Candle.OpenTimeUtc == T0.AddMinutes(1));
    }

    [Fact]
    public async Task History_ShortFromKraken_LongFromBitvavo()
    {
        var clock = new ManualClock(T0);
        var kraken = new HistoryTestSource("kraken", Crypto, TimeSpan.FromMinutes(720));
        var bitvavo = new HistoryTestSource("bitvavo", Crypto, null);
        var hub = new MarketDataHub([bitvavo, kraken], clock, new MarketDataHubOptions { MaintenanceInterval = TimeSpan.Zero });
        var shortRange = await hub.GetCandlesAsync("BTC/EUR", Timeframe.M1, T0.AddHours(-6), T0, default);
        Assert.Equal("kraken", shortRange[0].Source);
        var longRange = await hub.GetCandlesAsync("BTC/EUR", Timeframe.M1, T0.AddDays(-5), T0, default);
        Assert.Equal("bitvavo", longRange[0].Source);
        Assert.Single(kraken.Requests);
    }

    [Fact]
    public async Task History_FallsBackOnError()
    {
        var (hub, kraken, bitvavo, _, _, _, _) = Create();
        bitvavo.Candles = s => [new Candle { Symbol = s, Timeframe = Timeframe.M1, OpenTimeUtc = T0, Open = 1, High = 1, Low = 1, Close = 1, Source = "bitvavo" }];
        kraken.Candles = _ => throw new HttpRequestException("gesperrt");
        var c = await hub.GetCandlesAsync("BTC/EUR", Timeframe.M1, null, null, default);
        Assert.Equal("bitvavo", Assert.Single(c).Source);
    }

    [Fact]
    public async Task SuspendResume_ForwardsToSources_AndGapEvents()
    {
        var (hub, kraken, _, _, _, clock, log) = Create();
        await hub.StartAsync(default);
        await hub.SubscribeAsync(["BTC/EUR"], default);
        await hub.SuspendAsync(default);
        Assert.Equal(1, kraken.SuspendCalls);
        await hub.ResumeAsync(default);
        Assert.Equal(1, kraken.ResumeCalls);
        Assert.Equal("kraken", hub.GetActiveSource("BTC/EUR"));
        kraken.Emit(new GapDetected { Symbol = "BTC/EUR", Source = "kraken", AsOfUtc = clock.UtcNow, GapStartUtc = T0.AddHours(-1), GapEndUtc = T0, BackfilledCandles = [] });
        Assert.Single(log.Of<GapDetected>());
    }

    [Fact]
    public async Task Instruments_SynthesizedOrFromSearch()
    {
        var (hub, _, _, _, _, _, _) = Create();
        Assert.Equal(AssetClass.Crypto, hub.GetInstrument("SOL/EUR")!.AssetClass);
        Assert.Equal("SAP SE", hub.GetInstrument("DE0007164600")!.Name);
        Assert.Null(hub.GetInstrument("irgendwas"));
        Assert.Empty(await hub.SearchSymbolsAsync("BTC", default));
    }

    [Fact]
    public async Task EndToEnd_WithFakeSource()
    {
        var clock = new ManualClock(T0);
        var fake = new FakeMarketDataSource(clock, new SeededRandom(3));
        var hub = new MarketDataHub([fake], clock, new MarketDataHubOptions { MaintenanceInterval = TimeSpan.Zero });
        var log = new EventLog();
        hub.EventReceived += log.Add;
        await hub.SubscribeAsync(["BTC/EUR", "DE0007164600"], default);
        for (int i = 0; i < 10; i++) { clock.Advance(TimeSpan.FromSeconds(1)); fake.Step(); }
        Assert.NotNull(hub.GetLatestQuote("BTC/EUR")!.Bid);
        Assert.NotNull(hub.GetLatestOrderBook("BTC/EUR"));
        var stock = hub.GetLatestQuote("DE0007164600")!;
        Assert.Equal(15, stock.DelayedMinutes);
        Assert.False(stock.IsRealtime);
        var found = await hub.SearchSymbolsAsync("BTC", default);
        Assert.Equal("BTC/EUR", found[0].Symbol);
        Assert.Equal("fake", hub.GetInstrument("BTC/EUR")!.PrimarySource);
    }
}

public class FxTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Ecb_ParseXml_AndCrossRates()
    {
        var (date, rates) = EcbFxRateProvider.ParseXml(Fixture.Read("ecb/eurofxref-daily.xml"));
        Assert.Equal(new DateOnly(2026, 11, 6), date);
        Assert.Equal(1.0812m, rates["USD"]);
        var ecb = new EcbFxRateProvider(new HttpClient(new FakeHttpHandler(_ => FakeHttpHandler.NotFound())), new ManualClock(Now));
        ecb.SetRates(date, rates);
        Assert.Equal(1.0812m, ecb.GetRate("EUR", "USD"));
        Assert.Equal(Math.Round(1m / 1.0812m, 10), ecb.GetRate("USD", "EUR"));
        Assert.Equal(Math.Round(0.84105m / 1.0812m, 10), ecb.GetRate("USD", "GBP"));
        Assert.Equal(1m, ecb.GetRate("EUR", "EUR"));
        Assert.Null(ecb.GetRate("EUR", "XYZ"));
        Assert.Equal(new DateTimeOffset(2026, 11, 6, 15, 0, 0, TimeSpan.Zero), ecb.AsOfUtc); // 16:00 MEZ
        Assert.Contains("EZB", ecb.GetQuote("USD", "EUR")!.Source);
    }

    [Fact]
    public async Task Ecb_RefreshViaHttp()
    {
        var http = FakeHttpHandler.Client(_ => FakeHttpHandler.Json(Fixture.Read("ecb/eurofxref-daily.xml")), out var handler);
        var ecb = new EcbFxRateProvider(http, new ManualClock(Now));
        await ecb.RefreshAsync(default);
        Assert.Equal(1.0812m, ecb.GetRate("EUR", "USD"));
        Assert.Contains("eurofxref-daily.xml", handler.Requests[0].AbsolutePath);
    }

    [Fact]
    public async Task Composite_PrefersFreshIntradayQuote_FallsBackToEcb()
    {
        var clock = new ManualClock(Now);
        var kraken = new TestSource("kraken", SymbolHelper.IsCryptoPair);
        var hub = new MarketDataHub([kraken], clock, new MarketDataHubOptions { MaintenanceInterval = TimeSpan.Zero });
        var ecb = new EcbFxRateProvider(new HttpClient(new FakeHttpHandler(_ => FakeHttpHandler.NotFound())), clock);
        var (date, rates) = EcbFxRateProvider.ParseXml(Fixture.Read("ecb/eurofxref-daily.xml"));
        ecb.SetRates(date, rates);
        var fx = new CompositeFxRateProvider([new HubFxRateProvider(hub, clock), ecb]);

        Assert.Contains("EZB", fx.GetQuote("USD", "EUR")!.Source);
        await hub.SubscribeAsync(["USDC/EUR"], default);
        kraken.Emit(new QuoteTick { Symbol = "USDC/EUR", Source = "kraken", AsOfUtc = Now, Bid = 0.9248m, Ask = 0.9252m });
        var q = fx.GetQuote("USD", "EUR")!;
        Assert.Equal(0.9250m, q.Rate);
        Assert.Contains("USDC/EUR", q.Source);
        Assert.Equal(Math.Round(1m / 0.9250m, 10), fx.GetRate("EUR", "USD"));
        Assert.Equal(Now, fx.AsOfUtc);
        Assert.Equal(Math.Round(0.84105m / 1.0812m, 10), fx.GetRate("USD", "GBP")); // Kreuzkurs nur über EZB

        clock.Advance(TimeSpan.FromMinutes(6)); // Näherung veraltet
        Assert.Contains("EZB", fx.GetQuote("USD", "EUR")!.Source);
    }
}

public class FakeSourceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Step_ProducesTradesQuotesAndBook_Deterministically()
    {
        IReadOnlyList<MarketEvent> Run()
        {
            var clock = new ManualClock(T0);
            var f = new FakeMarketDataSource(clock, new SeededRandom(99));
            var all = new List<MarketEvent>();
            for (int i = 0; i < 20; i++) { clock.Advance(TimeSpan.FromMilliseconds(500)); all.AddRange(f.Step()); }
            return all;
        }
        var a = Run();
        var b = Run();
        Assert.Equal(a.Count, b.Count);
        Assert.Equal(a.OfType<TradeTick>().Select(t => t.Price), b.OfType<TradeTick>().Select(t => t.Price));
        Assert.Contains(a, e => e is TradeTick { Symbol: "BTC/EUR" });
        Assert.Contains(a, e => e is OrderBookUpdate { IsSnapshot: true, Symbol: "ETH/EUR" });
        Assert.Contains(a, e => e is OrderBookUpdate { IsSnapshot: false });
        Assert.All(a.OfType<QuoteTick>(), q => Assert.True(q.Bid < q.Ask));
        var stock = a.OfType<QuoteTick>().First(q => q.Symbol == "DE0007164600");
        Assert.Equal(DataQuality.Delayed, stock.Quality);
        Assert.Equal(15, stock.DelayedMinutes);
        Assert.DoesNotContain(a, e => e is OrderBookUpdate { Symbol: "DE0007164600" });
        Assert.All(a.OfType<TradeTick>(), t => Assert.True(t.Price > 0 && t.Quantity > 0));
    }

    [Fact]
    public void BookDeltas_ReconstructFakeBook()
    {
        var clock = new ManualClock(T0);
        var f = new FakeMarketDataSource(clock, new SeededRandom(5));
        var book = new OrderBookState();
        for (int i = 0; i < 30; i++)
            foreach (var u in f.Step().OfType<OrderBookUpdate>().Where(u => u.Symbol == "BTC/EUR")) book.Apply(u);
        var expected = f.GetOrderBookAsync("BTC/EUR", 100, default).Result!;
        Assert.Equal(expected.Bids, book.TopBids(100));
        Assert.Equal(expected.Asks, book.TopAsks(100));
    }

    [Fact]
    public async Task History_IsReproducible_AndContiguous()
    {
        var f1 = new FakeMarketDataSource(new ManualClock(T0), new SeededRandom(1));
        var f2 = new FakeMarketDataSource(new ManualClock(T0), new SeededRandom(2));
        var a = await f1.GetCandlesAsync("BTC/EUR", Timeframe.H1, T0.AddDays(-3), T0, default);
        var b = await f2.GetCandlesAsync("BTC/EUR", Timeframe.H1, T0.AddDays(-3), T0, default);
        Assert.Equal(72, a.Count);
        Assert.Equal(a.Select(c => c.Close), b.Select(c => c.Close));
        for (int i = 1; i < a.Count; i++)
        {
            Assert.Equal(a[i - 1].Close, a[i].Open);
            Assert.Equal(a[i - 1].OpenTimeUtc.AddHours(1), a[i].OpenTimeUtc);
            Assert.True(a[i].High >= Math.Max(a[i].Open, a[i].Close) && a[i].Low <= Math.Min(a[i].Open, a[i].Close));
        }
        Assert.Equal(62_000m, a[^1].Close);
    }

    [Fact]
    public void Diff_ProducesRemovalsAndChanges()
    {
        var d = FakeMarketDataSource.Diff([new(1m, 1m), new(2m, 1m)], [new(2m, 3m), new(3m, 1m)]);
        Assert.Contains(new OrderBookLevel(1m, 0m), d);
        Assert.Contains(new OrderBookLevel(2m, 3m), d);
        Assert.Contains(new OrderBookLevel(3m, 1m), d);
    }
}

public class DependencyInjectionTests
{
    [Fact]
    public void FakeMode_RegistersOnlyFake()
    {
        var sp = new ServiceCollection().AddMusterdepotMarketData(new DataSourceSettings { UseFakeData = true }).BuildServiceProvider();
        var hub = sp.GetRequiredService<IMarketDataHub>();
        Assert.Equal("fake", Assert.Single(hub.Sources).Name);
        Assert.Same(hub, sp.GetRequiredService<MarketDataHub>());
        Assert.NotNull(sp.GetRequiredService<IClock>());
        Assert.NotNull(sp.GetRequiredService<IRandomSource>());
        Assert.NotNull(sp.GetRequiredService<IIdGenerator>());
        var fx = sp.GetRequiredService<IFxRateProvider>();
        Assert.Equal(1.08m, fx.GetRate("EUR", "USD")); // fester Rückfallkurs im Fake-Modus
    }

    [Fact]
    public void LiveMode_RegistersEnabledSources_WithoutNetworkOnConstruction()
    {
        var services = new ServiceCollection();
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero));
        services.AddSingleton<IClock>(clock); // TryAdd respektiert vorhandene Registrierung
        services.AddMusterdepotMarketData(new DataSourceSettings { CoinbaseEnabled = false });
        services.AddMusterdepotMarketData(new DataSourceSettings()); // zweiter Aufruf ändert nichts
        var sp = services.BuildServiceProvider();
        var hub = sp.GetRequiredService<IMarketDataHub>();
        Assert.Equal(["kraken", "bitvavo", "gettex"], hub.Sources.Select(s => s.Name));
        Assert.Same(clock, sp.GetRequiredService<IClock>());
        Assert.IsType<KrakenSource>(hub.Sources[0]);
        Assert.IsType<BitvavoSource>(hub.Sources[1]);
        Assert.IsType<GettexSource>(hub.Sources[2]);
        Assert.IsType<CompositeFxRateProvider>(sp.GetRequiredService<IFxRateProvider>());
        Assert.NotNull(sp.GetRequiredService<EcbFxRateProvider>());
    }
}

/// <summary>Optionaler Live-Test gegen die echten Börsen (im Sandbox-Netz gesperrt).</summary>
public class LiveSmokeTests
{
    [Fact(Skip = "Live-Netz nötig – manuell aktivieren (MUSTERDEPOT_LIVE_TESTS=1)")]
    public async Task Kraken_RealConnection_DeliversTicker()
    {
        if (Environment.GetEnvironmentVariable("MUSTERDEPOT_LIVE_TESTS") != "1") return;
        var src = new KrakenSource(new KrakenOptions(), new HttpClient(), new Sources.ClientWebSocketConnectionFactory(), SystemClock.Instance, new SeededRandom(1));
        var log = new EventLog();
        src.EventReceived += log.Add;
        await src.SubscribeAsync(["BTC/EUR"], default);
        await src.StartAsync(default);
        await Wait.UntilAsync(() => log.Of<QuoteTick>().Count > 0, 20000);
        await src.StopAsync(default);
    }
}
