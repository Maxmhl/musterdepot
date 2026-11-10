using System.Globalization;
using System.Text;
using System.Web;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;
using Musterdepot.MarketData.Bitvavo;
using Musterdepot.MarketData.Coinbase;
using Musterdepot.MarketData.Infrastructure;
using Musterdepot.MarketData.Sources;

namespace Musterdepot.MarketData.Tests;

public class BitvavoParserTests
{
    private static readonly DateTimeOffset Received = new(2026, 11, 9, 10, 0, 6, TimeSpan.Zero);
    private readonly BitvavoMessageParser _p = new();

    [Fact]
    public void Ticker_MapsMarketToProgramSymbol()
    {
        var q = Assert.IsType<QuoteTick>(Assert.Single(_p.Parse(Fixture.Read("bitvavo/ticker.json"), Received).Events));
        Assert.Equal("BTC/EUR", q.Symbol);
        Assert.Equal(61998m, q.Bid);
        Assert.Equal(62001m, q.Ask);
        Assert.Equal(0.12840531m, q.BidSize);
        Assert.Equal(61999m, q.Last);
        Assert.Equal(Received, q.AsOfUtc);
    }

    [Fact]
    public void Ticker24h_ComputesChangePercent()
    {
        var q = Assert.IsType<QuoteTick>(Assert.Single(_p.Parse(Fixture.Read("bitvavo/ticker24h.json"), Received).Events));
        Assert.Equal(2.5m, q.ChangePercent); // (61500-60000)/60000
        Assert.Equal(62300m, q.High24h);
        Assert.Equal(1110.07120335m, q.Volume24h);
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero), q.AsOfUtc);
    }

    [Fact]
    public void Trade_Candle_Book()
    {
        var t = Assert.IsType<TradeTick>(Assert.Single(_p.Parse(Fixture.Read("bitvavo/trade.json"), Received).Events));
        Assert.Equal(61500m, t.Price);
        Assert.Equal(0.05m, t.Quantity);
        Assert.Equal(OrderSide.Sell, t.AggressorSide);
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 10, 0, 5, 123, TimeSpan.Zero), t.AsOfUtc);

        var c = Assert.IsType<CandleUpdate>(Assert.Single(_p.Parse(Fixture.Read("bitvavo/candle.json"), Received).Events)).Candle;
        Assert.Equal(Timeframe.M1, c.Timeframe);
        Assert.Equal(61490m, c.Open);
        Assert.Equal(1.25m, c.Volume);

        var snap = _p.Parse(Fixture.Read("bitvavo/getbook.json"), Received);
        Assert.Equal(1000, snap.Sequence);
        Assert.True(((OrderBookUpdate)snap.Events[0]).IsSnapshot);
        var delta = _p.Parse(Fixture.Read("bitvavo/book_1001.json"), Received);
        Assert.Equal(1001, delta.Sequence);
        Assert.False(((OrderBookUpdate)delta.Events[0]).IsSnapshot);
    }

    [Theory]
    [InlineData("bitvavo/subscribed.json", WsMessageKind.SubscriptionAck)]
    [InlineData("bitvavo/gettime.json", WsMessageKind.Pong)]
    [InlineData("bitvavo/error.json", WsMessageKind.Error)]
    public void Control(string file, WsMessageKind kind) => Assert.Equal(kind, _p.Parse(Fixture.Read(file), Received).Kind);

    [Fact]
    public void SubscribeMessage_UsesDashedMarkets()
    {
        var msg = BitvavoMessageParser.BuildSubscribe(["BTC/EUR"], book: true);
        Assert.Contains("\"markets\":[\"BTC-EUR\"]", msg);
        foreach (var ch in new[] { "ticker", "ticker24h", "trades", "candles", "book" }) Assert.Contains($"\"name\":\"{ch}\"", msg);
        Assert.Contains("\"interval\":[\"1m\"]", msg);
    }

    [Fact]
    public void Markets_ToInstruments()
    {
        var m = BitvavoRestClient.ParseMarkets(Fixture.Read("bitvavo/markets.json"));
        Assert.Equal(4, m.Count);
        var btc = m[0].ToInstrument();
        Assert.Equal("BTC/EUR", btc.Symbol);
        Assert.Equal("BTC-EUR", btc.SourceSymbol);
        Assert.Equal(0.00000001m, btc.QuantityStep);
    }
}

public class BitvavoSourceTests
{
    [Fact]
    public async Task BookNonceGap_TriggersResync()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero));
        var ws = new FakeWebSocketFactory();
        var src = new BitvavoSource(new BitvavoOptions { PingInterval = TimeSpan.FromHours(1) },
            new HttpClient(new FakeHttpHandler(_ => FakeHttpHandler.NotFound())), ws, clock, new SeededRandom(1));
        src.SetMarkets(BitvavoRestClient.ParseMarkets(Fixture.Read("bitvavo/markets.json")));
        var log = new EventLog();
        src.EventReceived += log.Add;
        await src.SubscribeAsync(["BTC/EUR"], default);
        await src.StartAsync(default);
        await Wait.UntilAsync(() => src.State == ConnectionState.Connected && ws.Created.Count == 1 && ws.Last.Sent.Count >= 2);
        Assert.Contains(ws.Last.Sent, m => m.Contains("\"action\":\"getBook\""));

        ws.Last.Push(Fixture.Read("bitvavo/book_1001.json")); // vor dem Snapshot → verworfen
        ws.Last.Push(Fixture.Read("bitvavo/getbook.json"));
        ws.Last.Push(Fixture.Read("bitvavo/book_1001.json"));
        await Wait.UntilAsync(() => log.Of<OrderBookUpdate>().Count == 2);
        var ob = await src.GetOrderBookAsync("BTC/EUR", 10, default);
        Assert.Equal(61500m, ob!.BestBid);
        Assert.Equal(61501m, ob.BestAsk);
        Assert.Equal(2.5m, ob.Asks.Single(a => a.Price == 61502m).Quantity);

        int sent = ws.Last.Sent.Count;
        ws.Last.Push(Fixture.Read("bitvavo/book_1005.json")); // Lücke 1002..1004
        await Wait.UntilAsync(() => src.NonceResyncs == 1 && ws.Last.Sent.Count > sent);
        Assert.Contains(ws.Last.Sent.Skip(sent), m => m.Contains("getBook"));
        Assert.Equal(2, log.Of<OrderBookUpdate>().Count);
        await src.StopAsync(default);
    }

    [Fact]
    public async Task Candles_PageBackwardsUntilStart()
    {
        // 3000 Minuten → drei Seiten à max. 1440 (neueste zuerst)
        var to = new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);
        var from = to.AddMinutes(-3000);
        var http = FakeHttpHandler.Client(req =>
        {
            var q = HttpUtility.ParseQueryString(req.RequestUri!.Query);
            var start = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(q["start"]!, CultureInfo.InvariantCulture));
            var end = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(q["end"]!, CultureInfo.InvariantCulture));
            var limit = int.Parse(q["limit"]!, CultureInfo.InvariantCulture);
            var sb = new StringBuilder("[");
            int n = 0;
            for (var t = end.AddMinutes(-1); t >= start && n < limit; t = t.AddMinutes(-1), n++)
            {
                if (n > 0) sb.Append(',');
                sb.Append(CultureInfo.InvariantCulture, $"[{t.ToUnixTimeMilliseconds()},\"100\",\"101\",\"99\",\"100.5\",\"1.5\"]");
            }
            sb.Append(']');
            return FakeHttpHandler.Json(sb.ToString());
        }, out var handler);
        var rest = new BitvavoRestClient(http) { PageDelay = TimeSpan.Zero };
        var candles = await rest.GetCandlesAsync("BTC/EUR", Timeframe.M1, from, to, to, default);
        Assert.Equal(3000, candles.Count);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Contains("/BTC-EUR/candles", r.AbsolutePath));
        Assert.Equal(from, candles[0].OpenTimeUtc);
        Assert.Equal(to.AddMinutes(-1), candles[^1].OpenTimeUtc);
        Assert.Equal(candles.Select(c => c.OpenTimeUtc).Distinct().Count(), candles.Count);
        Assert.All(candles, c => Assert.True(c.IsClosed));
    }
}

public class CoinbaseTests
{
    private static readonly DateTimeOffset Received = new(2026, 11, 9, 10, 0, 6, TimeSpan.Zero);
    private readonly CoinbaseMessageParser _p = new();

    [Fact]
    public void Ticker_And_Match()
    {
        var q = Assert.IsType<QuoteTick>(Assert.Single(_p.Parse(Fixture.Read("coinbase/ticker.json"), Received).Events));
        Assert.Equal("BTC/EUR", q.Symbol);
        Assert.Equal(61500.11m, q.Bid);
        Assert.Equal(61500.12m, q.Ask);
        Assert.Equal(2.5002m, q.ChangePercent);
        var t = Assert.IsType<TradeTick>(Assert.Single(_p.Parse(Fixture.Read("coinbase/match.json"), Received).Events));
        Assert.Equal(OrderSide.Buy, t.AggressorSide); // Maker hat verkauft → Käufer war Aggressor
        Assert.Equal(0.005m, t.Quantity);
        Assert.Equal("91234568", t.TradeId);
    }

    [Fact]
    public void Level2_SnapshotAndUpdate_ApplyToBook()
    {
        var book = new OrderBookState();
        book.Apply((OrderBookUpdate)_p.Parse(Fixture.Read("coinbase/snapshot.json"), Received).Events[0]);
        var upd = (OrderBookUpdate)_p.Parse(Fixture.Read("coinbase/l2update.json"), Received).Events[0];
        Assert.Single(upd.Bids);
        Assert.Single(upd.Asks);
        book.Apply(upd);
        Assert.Equal(61500.00m, book.TopBids(1)[0].Price);
        Assert.Equal(61500.12m, book.TopAsks(1)[0].Price);
        Assert.Equal(3, book.AskCount);
    }

    [Theory]
    [InlineData("coinbase/heartbeat.json", WsMessageKind.Heartbeat)]
    [InlineData("coinbase/subscriptions.json", WsMessageKind.SubscriptionAck)]
    [InlineData("coinbase/error.json", WsMessageKind.Error)]
    public void Control(string file, WsMessageKind kind) => Assert.Equal(kind, _p.Parse(Fixture.Read(file), Received).Kind);

    [Fact]
    public void RestCandles_ColumnOrderLowHighOpenClose()
    {
        var c = CoinbaseRestClient.ParseCandles(Fixture.Read("coinbase/rest_candles.json"), "BTC/EUR", Timeframe.M1, Received);
        Assert.Equal(4, c.Count);
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero), c[0].OpenTimeUtc);
        Assert.Equal(61900.0m, c[0].Open);
        Assert.Equal(61950.5m, c[0].High);
        Assert.Equal(61880.1m, c[0].Low);
        Assert.Equal(61940.2m, c[0].Close);
    }

    [Fact]
    public async Task RestCandles_FetchedInChunksOf300()
    {
        var to = new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);
        var http = FakeHttpHandler.Client(_ => FakeHttpHandler.Json(Fixture.Read("coinbase/rest_candles.json")), out var handler);
        var rest = new CoinbaseRestClient(http) { PageDelay = TimeSpan.Zero };
        await rest.GetCandlesAsync("BTC/EUR", Timeframe.M1, to.AddMinutes(-700), to.AddMinutes(10), to, default);
        Assert.Equal(3, handler.Requests.Count); // 700+10 Minuten / 300
        Assert.All(handler.Requests, r => Assert.Contains("granularity=60", r.Query));
    }

    [Fact]
    public void Products_FilterDisabled()
    {
        var p = CoinbaseRestClient.ParseProducts(Fixture.Read("coinbase/products.json"));
        Assert.Equal(2, p.Count);
        Assert.Equal("BTC/EUR", p[0].Symbol);
        Assert.Equal("BTC-EUR", p[0].SourceSymbol);
    }

    [Fact]
    public void SubscribeMessage()
    {
        var msg = CoinbaseMessageParser.BuildSubscribe(["BTC/EUR"], level2: true);
        Assert.Contains("\"product_ids\":[\"BTC-EUR\"]", msg);
        Assert.Contains("level2_batch", msg);
        Assert.Contains("matches", msg);
    }
}
