using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;
using Musterdepot.MarketData.Infrastructure;
using Musterdepot.MarketData.Kraken;
using Musterdepot.MarketData.Sources;

namespace Musterdepot.MarketData.Tests;

public class KrakenParserTests
{
    private static readonly DateTimeOffset Received = new(2026, 11, 9, 10, 15, 2, TimeSpan.Zero);
    private readonly KrakenMessageParser _p = new();

    [Fact]
    public void Ticker_IsParsedAsQuoteTick()
    {
        var r = _p.Parse(Fixture.Read("kraken/ticker.json"), Received);
        Assert.Equal(WsMessageKind.Data, r.Kind);
        var q = Assert.IsType<QuoteTick>(Assert.Single(r.Events));
        Assert.Equal("BTC/EUR", q.Symbol);
        Assert.Equal("kraken", q.Source);
        Assert.Equal(61999.9m, q.Bid);
        Assert.Equal(62000.0m, q.Ask);
        Assert.Equal(0.52143201m, q.BidSize);
        Assert.Equal(1834.27183654m, q.Volume24h);
        Assert.Equal(1.79m, q.ChangePercent);
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 10, 15, 0, 512, TimeSpan.Zero).AddTicks(3450), q.AsOfUtc);
        Assert.Equal(Received, q.ReceivedUtc);
        Assert.True(q.IsRealtime);
    }

    [Fact]
    public void Trade_IsParsedWithSideAndId()
    {
        var r = _p.Parse(Fixture.Read("kraken/trade.json"), Received);
        var trades = r.Events.Cast<TradeTick>().ToList();
        Assert.Equal(2, trades.Count);
        Assert.Equal(62000.0m, trades[0].Price);
        Assert.Equal(0.015m, trades[0].Quantity);
        Assert.Equal(OrderSide.Buy, trades[0].AggressorSide);
        Assert.Equal("81234567", trades[0].TradeId);
        Assert.Equal(OrderSide.Sell, trades[1].AggressorSide);
    }

    [Fact]
    public void OhlcSnapshot_MarksOlderCandlesClosed_AndParsesNanoseconds()
    {
        var r = _p.Parse(Fixture.Read("kraken/ohlc_snapshot.json"), Received);
        var c = r.Events.Cast<CandleUpdate>().Select(e => e.Candle).ToList();
        Assert.Equal(2, c.Count);
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 10, 13, 0, TimeSpan.Zero), c[0].OpenTimeUtc);
        Assert.True(c[0].IsClosed);
        Assert.False(c[1].IsClosed);
        Assert.Equal(Timeframe.M1, c[0].Timeframe);
        Assert.Equal(41, c[0].TradeCount);
        Assert.Equal(3.21456789m, c[0].Volume);
    }

    [Fact]
    public void Book_SnapshotAndUpdate()
    {
        var snap = Assert.IsType<OrderBookUpdate>(Assert.Single(_p.Parse(Fixture.Read("kraken/book_snapshot.json"), Received).Events));
        Assert.True(snap.IsSnapshot);
        Assert.Equal(10, snap.Bids.Count);
        Assert.Equal(10, snap.Asks.Count);
        Assert.Equal(3281593206L, snap.Checksum);
        var upd = Assert.IsType<OrderBookUpdate>(Assert.Single(_p.Parse(Fixture.Read("kraken/book_update.json"), Received).Events));
        Assert.False(upd.IsSnapshot);
        Assert.Contains(upd.Asks, l => l.Price == 62000.1m && l.Quantity == 0m);
    }

    [Theory]
    [InlineData("kraken/heartbeat.json", WsMessageKind.Heartbeat)]
    [InlineData("kraken/pong.json", WsMessageKind.Pong)]
    [InlineData("kraken/subscribe_ack.json", WsMessageKind.SubscriptionAck)]
    [InlineData("kraken/subscribe_error.json", WsMessageKind.Error)]
    [InlineData("kraken/status.json", WsMessageKind.Status)]
    public void ControlMessages(string file, WsMessageKind kind)
    {
        var r = _p.Parse(Fixture.Read(file), Received);
        Assert.Equal(kind, r.Kind);
        Assert.Empty(r.Events);
        if (kind == WsMessageKind.Error) Assert.Contains("FOO/EUR", r.Message);
    }

    [Fact]
    public void SubscribeMessage_HasChannelSymbolsAndDepth()
    {
        var msg = KrakenMessageParser.BuildSubscribe("book", ["BTC/EUR", "ETH/EUR"], depth: 25);
        Assert.Contains("\"method\":\"subscribe\"", msg);
        Assert.Contains("\"channel\":\"book\"", msg);
        Assert.Contains("\"symbol\":[\"BTC/EUR\",\"ETH/EUR\"]", msg);
        Assert.Contains("\"depth\":25", msg);
    }
}

public class KrakenChecksumTests
{
    [Fact]
    public void Checksum_MatchesIndependentZlibComputation_ForSnapshotAndDelta()
    {
        var p = new KrakenMessageParser();
        var now = DateTimeOffset.UtcNow;
        var book = new OrderBookState(10);
        var snap = (OrderBookUpdate)p.Parse(Fixture.Read("kraken/book_snapshot.json"), now).Events[0];
        book.Apply(snap);
        Assert.Equal((uint)snap.Checksum!.Value, KrakenChecksum.Compute(book.TopAsks(10), book.TopBids(10), 1, 8));

        var upd = (OrderBookUpdate)p.Parse(Fixture.Read("kraken/book_update.json"), now).Events[0];
        book.Apply(upd);
        Assert.Equal(10, book.BidCount); // auf Tiefe 10 gekürzt
        Assert.Equal((uint)upd.Checksum!.Value, KrakenChecksum.Compute(book.TopAsks(10), book.TopBids(10), 1, 8));
    }

    [Theory]
    [InlineData("62000.1", 1, "620001")]
    [InlineData("0.00100000", 8, "100000")]
    [InlineData("0.5", 8, "50000000")]
    public void Format_RemovesDotAndLeadingZeros(string value, int precision, string expected)
        => Assert.Equal(expected, KrakenChecksum.Format(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), precision));

    [Fact]
    public void Crc32_KnownVector() => Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"));
}

public class KrakenRestTests
{
    [Fact]
    public void AssetPairs_NormalizeXbtAndXdg()
    {
        var pairs = KrakenRestClient.ParseAssetPairs(Fixture.Read("kraken/rest_assetpairs.json"));
        var btc = Assert.Single(pairs, p => p.Symbol == "BTC/EUR");
        Assert.Equal("XBTEUR", btc.AltName);
        Assert.Equal(1, btc.PricePrecision);
        Assert.Equal(8, btc.QtyPrecision);
        Assert.Contains(pairs, p => p.Symbol == "DOGE/EUR");
        Assert.Contains(pairs, p => p.Symbol == "USDC/EUR");
        var inst = btc.ToInstrument();
        Assert.Equal(0.1m, inst.TickSize);
        Assert.Equal(0.00000001m, inst.QuantityStep);
        Assert.Equal(0.00005m, inst.MinQuantity);
        Assert.Equal("EUR", inst.QuoteCurrency);
    }

    [Fact]
    public void Ohlc_ParsesSortedCandles_LastOpen()
    {
        var now = new DateTimeOffset(2026, 11, 9, 10, 3, 30, TimeSpan.Zero);
        var c = KrakenRestClient.ParseOhlc(Fixture.Read("kraken/rest_ohlc.json"), "BTC/EUR", Timeframe.M1, now);
        Assert.Equal(4, c.Count);
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero), c[0].OpenTimeUtc);
        Assert.Equal(61900.0m, c[0].Open);
        Assert.Equal(1.5231m, c[0].Volume);
        Assert.Equal(37, c[0].TradeCount);
        Assert.True(c[2].IsClosed);
        Assert.False(c[3].IsClosed);
    }

    [Fact]
    public void Depth_AndError()
    {
        var (bids, asks) = KrakenRestClient.ParseDepth(Fixture.Read("kraken/rest_depth.json"));
        Assert.Equal(62000.0m, bids[0].Price);
        Assert.Equal(62000.1m, asks[0].Price);
        Assert.Throws<KrakenApiException>(() => KrakenRestClient.ParseOhlc(Fixture.Read("kraken/rest_error.json"), "X/EUR", Timeframe.M1, DateTimeOffset.UtcNow));
    }
}

public class KrakenSourceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 10, 0, 30, TimeSpan.Zero);

    private static (KrakenSource Source, FakeWebSocketFactory Ws, ManualClock Clock, EventLog Log, FakeHttpHandler Http) Create(KrakenOptions? options = null)
    {
        var clock = new ManualClock(T0);
        var ws = new FakeWebSocketFactory();
        var http = FakeHttpHandler.Client(req => req.RequestUri!.AbsolutePath.EndsWith("/OHLC")
            ? FakeHttpHandler.Json(Fixture.Read("kraken/rest_ohlc.json"))
            : FakeHttpHandler.Json(Fixture.Read("kraken/rest_assetpairs.json")), out var handler);
        var opts = options ?? new KrakenOptions
        {
            Backoff = new BackoffOptions { InitialDelay = TimeSpan.FromMilliseconds(1), MaxDelay = TimeSpan.FromMilliseconds(5) },
            PingInterval = TimeSpan.FromHours(1),
        };
        var src = new KrakenSource(opts, http, ws, clock, new SeededRandom(1));
        src.SetPairs(KrakenRestClient.ParseAssetPairs(Fixture.Read("kraken/rest_assetpairs.json")));
        var log = new EventLog();
        src.EventReceived += log.Add;
        return (src, ws, clock, log, handler);
    }

    [Fact]
    public async Task Connects_SubscribesAllChannels_AndPublishesEvents()
    {
        var (src, ws, _, log, _) = Create();
        await src.SubscribeAsync(["BTC/EUR", "FOO/BAR"], default); // FOO/BAR wird nicht unterstützt
        await src.StartAsync(default);
        await Wait.UntilAsync(() => src.State == ConnectionState.Connected && ws.Created.Count == 1 && ws.Last.Sent.Count >= 4, because: "verbunden + abonniert");
        var sent = ws.Last.Sent;
        Assert.Contains(sent, m => m.Contains("\"channel\":\"ticker\""));
        Assert.Contains(sent, m => m.Contains("\"channel\":\"trade\""));
        Assert.Contains(sent, m => m.Contains("\"channel\":\"ohlc\"") && m.Contains("\"interval\":1"));
        Assert.Contains(sent, m => m.Contains("\"channel\":\"book\"") && m.Contains("\"depth\":10"));
        Assert.DoesNotContain(sent, m => m.Contains("FOO/BAR"));

        ws.Last.Push(Fixture.Read("kraken/ticker.json"));
        ws.Last.Push(Fixture.Read("kraken/trade.json"));
        await Wait.UntilAsync(() => log.Of<TradeTick>().Count == 2);
        Assert.Single(log.Of<QuoteTick>());
        Assert.Contains(log.Of<ConnectionStatusChanged>(), s => s.State == ConnectionState.Connected);
        await src.StopAsync(default);
        Assert.Equal(ConnectionState.Disconnected, src.State);
    }

    [Fact]
    public async Task OrderBook_MaintainedWithChecksum_ResubscribesOnMismatch()
    {
        var (src, ws, _, log, _) = Create();
        await src.SubscribeAsync(["BTC/EUR"], default);
        await src.StartAsync(default);
        await Wait.UntilAsync(() => src.State == ConnectionState.Connected && ws.Created.Count == 1);
        ws.Last.Push(Fixture.Read("kraken/book_snapshot.json"));
        ws.Last.Push(Fixture.Read("kraken/book_update.json"));
        await Wait.UntilAsync(() => log.Of<OrderBookUpdate>().Count == 2);
        Assert.Equal(0, src.ChecksumMismatches);
        var ob = await src.GetOrderBookAsync("BTC/EUR", 5, default);
        Assert.Equal(62000.0m, ob!.BestBid);
        Assert.Equal(62000.6m, ob.BestAsk);

        int sentBefore = ws.Last.Sent.Count;
        ws.Last.Push(Fixture.Read("kraken/book_update_bad_checksum.json"));
        await Wait.UntilAsync(() => src.ChecksumMismatches == 1 && ws.Last.Sent.Count >= sentBefore + 2);
        var resub = ws.Last.Sent.Skip(sentBefore).ToList();
        Assert.Contains(resub, m => m.Contains("\"method\":\"unsubscribe\"") && m.Contains("\"channel\":\"book\""));
        Assert.Contains(resub, m => m.Contains("\"method\":\"subscribe\"") && m.Contains("\"channel\":\"book\""));
        Assert.Contains(log.Of<ConnectionStatusChanged>(), s => s.Message?.Contains("Prüfsumme") == true);
        await src.StopAsync(default);
    }

    [Fact]
    public async Task OhlcStream_EmitsClosedCandleOnRollover()
    {
        var (src, ws, _, log, _) = Create();
        await src.SubscribeAsync(["BTC/EUR"], default);
        await src.StartAsync(default);
        await Wait.UntilAsync(() => src.State == ConnectionState.Connected && ws.Created.Count == 1);
        ws.Last.Push(Fixture.Read("kraken/ohlc_snapshot.json"));
        ws.Last.Push(Fixture.Read("kraken/ohlc_update.json"));
        await Wait.UntilAsync(() => log.Of<CandleUpdate>().Count >= 4);
        var candles = log.Of<CandleUpdate>().Select(c => c.Candle).ToList();
        // Laufende 10:14-Kerze wird beim Wechsel auf 10:15 als abgeschlossen gemeldet.
        Assert.Contains(candles, c => c.OpenTimeUtc.Minute == 14 && c.IsClosed);
        Assert.False(candles[^1].IsClosed);
        Assert.Equal(15, candles[^1].OpenTimeUtc.Minute);
        await src.StopAsync(default);
    }

    [Fact]
    public async Task Reconnects_AfterServerClose_AndResubscribes()
    {
        var (src, ws, _, log, _) = Create();
        await src.SubscribeAsync(["BTC/EUR"], default);
        await src.StartAsync(default);
        await Wait.UntilAsync(() => ws.Created.Count == 1 && src.State == ConnectionState.Connected);
        ws.Last.ServerClose();
        await Wait.UntilAsync(() => ws.Created.Count == 2 && src.State == ConnectionState.Connected && ws.Last.Sent.Count >= 4);
        Assert.Contains(log.Of<ConnectionStatusChanged>(), s => s.State == ConnectionState.Reconnecting);
        Assert.Contains(ws.Last.Sent, m => m.Contains("BTC/EUR"));
        Assert.Equal(2, src.ConnectCount);
        await src.StopAsync(default);
    }

    [Fact]
    public async Task ReceiveTimeout_TriggersReconnect()
    {
        var (src, ws, _, log, _) = Create(new KrakenOptions
        {
            ReceiveTimeout = TimeSpan.FromMilliseconds(100),
            PingInterval = TimeSpan.FromHours(1),
            Backoff = new BackoffOptions { InitialDelay = TimeSpan.FromMilliseconds(1), MaxDelay = TimeSpan.FromMilliseconds(5) },
        });
        await src.SubscribeAsync(["BTC/EUR"], default);
        await src.StartAsync(default);
        await Wait.UntilAsync(() => ws.Created.Count >= 2, because: "Timeout erkannt");
        Assert.Contains(log.Of<ConnectionStatusChanged>(), s => s.Message?.Contains("Keine Daten") == true);
        await src.StopAsync(default);
    }

    [Fact]
    public async Task FailedConnects_StopAfterMaxConsecutiveFailures()
    {
        var factory = new FakeWebSocketFactory();
        for (int i = 0; i < 5; i++) factory.Plan(new FakeWebSocket { FailConnect = true });
        var src = new KrakenSource(new KrakenOptions
        {
            MaxConsecutiveFailures = 3,
            Backoff = new BackoffOptions { InitialDelay = TimeSpan.FromMilliseconds(1), MaxDelay = TimeSpan.FromMilliseconds(2) },
        }, new HttpClient(new FakeHttpHandler(_ => FakeHttpHandler.NotFound())), factory, new ManualClock(T0), new SeededRandom(1));
        await src.StartAsync(default);
        await Wait.UntilAsync(() => src.State == ConnectionState.Failed);
        Assert.Equal(3, factory.Created.Count);
        await src.StopAsync(default);
    }

    [Fact]
    public async Task RateLimiter_BlocksConnectionsBeyondLimit()
    {
        var clock = new ManualClock(T0);
        var limiter = new ConnectionRateLimiter(2, TimeSpan.FromMinutes(10), clock);
        var factory = new FakeWebSocketFactory();
        var src = new KrakenSource(new KrakenOptions
        {
            Backoff = new BackoffOptions { InitialDelay = TimeSpan.FromMilliseconds(1), MaxDelay = TimeSpan.FromMilliseconds(2) },
            PingInterval = TimeSpan.FromHours(1),
        }, new HttpClient(new FakeHttpHandler(_ => FakeHttpHandler.NotFound())), factory, clock, new SeededRandom(1), limiter);
        var delays = new List<TimeSpan>();
        src.DelayAsync = (d, ct) => { lock (delays) delays.Add(d); return Task.Delay(5, ct); };
        var log = new EventLog();
        src.EventReceived += log.Add;
        await src.StartAsync(default);
        await Wait.UntilAsync(() => factory.Created.Count == 1 && src.State == ConnectionState.Connected);
        factory.Last.ServerClose();
        await Wait.UntilAsync(() => factory.Created.Count == 2 && src.State == ConnectionState.Connected);
        factory.Last.ServerClose();
        await Wait.UntilAsync(() => log.Of<ConnectionStatusChanged>().Any(s => s.Message?.Contains("Verbindungslimit") == true));
        Assert.Equal(2, factory.Created.Count);
        clock.Advance(TimeSpan.FromMinutes(10));
        await Wait.UntilAsync(() => factory.Created.Count == 3, because: "nach Ablauf des Fensters wieder erlaubt");
        await src.StopAsync(default);
    }

    [Fact]
    public async Task Resume_BackfillsMissedCandlesViaRest_AndReportsGap()
    {
        var (src, ws, clock, log, http) = Create();
        await src.SubscribeAsync(["BTC/EUR"], default);
        await src.StartAsync(default);
        await Wait.UntilAsync(() => src.State == ConnectionState.Connected);

        await src.SuspendAsync(default);
        Assert.Equal(ConnectionState.Suspended, src.State);
        Assert.Equal(T0, src.SuspendedAtUtc);
        clock.Set(new DateTimeOffset(2026, 11, 9, 10, 4, 30, TimeSpan.Zero));
        await src.ResumeAsync(default);

        var gap = Assert.Single(log.Of<GapDetected>());
        Assert.Equal("BTC/EUR", gap.Symbol);
        Assert.Equal(T0, gap.GapStartUtc);
        Assert.Equal(clock.UtcNow, gap.GapEndUtc);
        Assert.Equal(DataQuality.Backfill, gap.Quality);
        Assert.Equal(4, gap.BackfilledCandles.Count);
        Assert.All(gap.BackfilledCandles, c => Assert.True(c.IsBackfill && c.IsClosed));
        Assert.Equal(gap.BackfilledCandles.OrderBy(c => c.OpenTimeUtc).Select(c => c.OpenTimeUtc), gap.BackfilledCandles.Select(c => c.OpenTimeUtc));
        var ohlcRequest = Assert.Single(http.Requests, u => u.AbsolutePath.EndsWith("/OHLC"));
        Assert.Contains("pair=XBTEUR", ohlcRequest.Query);
        Assert.Contains("interval=1", ohlcRequest.Query);
        await Wait.UntilAsync(() => src.State == ConnectionState.Connected && ws.Created.Count == 2);
        await src.StopAsync(default);
    }
}
