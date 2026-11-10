using FsCheck;
using FsCheck.Fluent;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;
using Musterdepot.MarketData.Infrastructure;

namespace Musterdepot.MarketData.Tests;

public class OrderBookStateTests
{
    [Fact]
    public void Delta_RemovesUpdatesAndAddsLevels_SortedCorrectly()
    {
        var b = new OrderBookState();
        b.ApplySnapshot([new(100m, 1m), new(99m, 2m), new(98m, 3m)], [new(101m, 1m), new(102m, 2m)]);
        b.ApplyDelta([new(99m, 0m), new(100.5m, 4m), new(98m, 1.5m)], [new(101m, 0m), new(103m, 7m)]);
        Assert.Equal([new OrderBookLevel(100.5m, 4m), new(100m, 1m), new(98m, 1.5m)], b.TopBids(10));
        Assert.Equal([new OrderBookLevel(102m, 2m), new(103m, 7m)], b.TopAsks(10));
        var snap = b.ToSnapshot("X", "test");
        Assert.Equal(100.5m, snap.BestBid);
        Assert.Equal(102m, snap.BestAsk);
    }

    [Fact]
    public void Truncates_ToMaxDepth()
    {
        var b = new OrderBookState(2);
        b.ApplySnapshot([new(100m, 1m), new(99m, 1m), new(98m, 1m)], [new(101m, 1m)]);
        Assert.Equal(2, b.BidCount);
        b.ApplyDelta([new(100.5m, 1m)], []);
        Assert.Equal([100.5m, 100m], b.TopBids(5).Select(l => l.Price));
    }

    [FsCheck.Xunit.Property(MaxTest = 200)]
    public bool Deltas_MatchDictionaryModel(int[] prices, int[] quantities)
    {
        var book = new OrderBookState();
        var model = new Dictionary<decimal, decimal>();
        book.ApplySnapshot([], []);
        for (int i = 0; i < Math.Min(prices.Length, quantities.Length); i++)
        {
            var p = 1000m + Math.Abs(prices[i] % 50) * 0.5m;
            var q = Math.Abs(quantities[i] % 4) * 0.25m; // 0 = entfernen
            book.ApplyDelta([new(p, q)], []);
            if (q == 0) model.Remove(p); else model[p] = q;
        }
        var expected = model.OrderByDescending(kv => kv.Key).Select(kv => new OrderBookLevel(kv.Key, kv.Value)).ToList();
        return expected.SequenceEqual(book.TopBids(int.MaxValue));
    }
}

public class CandleAggregationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Trades_BuildM1_AndCloseOnRollover()
    {
        var agg = new TradeCandleAggregator([Timeframe.M1, Timeframe.M5], TimeSpan.Zero);
        agg.AddTrade("BTC/EUR", "k", T0.AddSeconds(5), 100m, 1m, T0);
        agg.AddTrade("BTC/EUR", "k", T0.AddSeconds(30), 105m, 2m, T0);
        agg.AddTrade("BTC/EUR", "k", T0.AddSeconds(50), 98m, 0.5m, T0);
        var r = agg.AddTrade("BTC/EUR", "k", T0.AddSeconds(61), 101m, 1m, T0);
        var closed = Assert.Single(r, c => c.Candle.IsClosed).Candle;
        Assert.Equal(Timeframe.M1, closed.Timeframe);
        Assert.Equal((100m, 105m, 98m, 98m, 3.5m, 3), (closed.Open, closed.High, closed.Low, closed.Close, closed.Volume, closed.TradeCount));
        var m5 = agg.GetCurrent("BTC/EUR", Timeframe.M5)!;
        Assert.Equal((100m, 105m, 98m, 101m, 4.5m), (m5.Open, m5.High, m5.Low, m5.Close, m5.Volume));
        Assert.False(m5.IsClosed);
        // verspäteter Trade für die abgeschlossene Minute wird ignoriert
        Assert.DoesNotContain(agg.AddTrade("BTC/EUR", "k", T0.AddSeconds(59), 200m, 1m, T0), c => c.Candle.Timeframe == Timeframe.M1);
    }

    [Fact]
    public void OpenUpdates_AreThrottled_AndCloseDueRespectsDelay()
    {
        var agg = new TradeCandleAggregator([Timeframe.M1], TimeSpan.FromSeconds(1));
        Assert.Single(agg.AddTrade("A", "s", T0, 1m, 1m, T0));
        Assert.Empty(agg.AddTrade("A", "s", T0.AddMilliseconds(100), 1m, 1m, T0.AddMilliseconds(100)));
        Assert.Single(agg.AddTrade("A", "s", T0.AddMilliseconds(1500), 1m, 1m, T0.AddMilliseconds(1500)));
        agg.AddTrade("ISIN", "g", T0, 1m, 1m, T0.AddMinutes(15), DataQuality.Delayed, 15);
        var due = agg.CloseDue(T0.AddSeconds(63), TimeSpan.FromSeconds(2));
        Assert.Equal("A", Assert.Single(due).Candle.Symbol); // ISIN ist 15 min verzögert
        var later = agg.CloseDue(T0.AddMinutes(16).AddSeconds(3), TimeSpan.FromSeconds(2));
        Assert.Equal(15, Assert.Single(later).DelayedMinutes);
    }

    [FsCheck.Xunit.Property(MaxTest = 100)]
    public bool Resample_PreservesVolumeAndExtremes(byte[] moves)
    {
        var candles = new List<Candle>();
        decimal price = 100m;
        for (int i = 0; i < moves.Length; i++)
        {
            var next = Math.Max(1m, price + (moves[i] % 11 - 5) * 0.1m);
            candles.Add(new Candle { Symbol = "X", Timeframe = Timeframe.M1, OpenTimeUtc = T0.AddMinutes(i), Open = price, Close = next, High = Math.Max(price, next) + 0.05m, Low = Math.Min(price, next) - 0.05m, Volume = moves[i] });
            price = next;
        }
        var h1 = TradeCandleAggregator.Resample(candles, Timeframe.M5);
        return h1.Sum(c => c.Volume) == candles.Sum(c => c.Volume)
            && (candles.Count == 0 || (h1.Max(c => c.High) == candles.Max(c => c.High) && h1.Min(c => c.Low) == candles.Min(c => c.Low)
                && h1[0].Open == candles[0].Open && h1[^1].Close == candles[^1].Close))
            && h1.All(c => c.OpenTimeUtc == Timeframe.M5.Floor(c.OpenTimeUtc));
    }
}

public class ReconnectTests
{
    [Fact]
    public void Backoff_GrowsExponentially_WithinJitter_AndIsCapped()
    {
        var b = new ExponentialBackoff(new BackoffOptions { InitialDelay = TimeSpan.FromSeconds(1), MaxDelay = TimeSpan.FromSeconds(30), Multiplier = 2, JitterFactor = 0.25 }, new SeededRandom(42));
        for (int attempt = 0; attempt < 10; attempt++)
        {
            var nominal = Math.Min(30, Math.Pow(2, attempt));
            var d = b.GetDelay(attempt).TotalSeconds;
            Assert.InRange(d, nominal * 0.75 - 1e-9, Math.Min(30, nominal * 1.25) + 1e-9);
        }
        Assert.True(b.GetDelay(100).TotalSeconds <= 30);
    }

    [Fact]
    public void Backoff_JitterSpreadsValues()
    {
        var b = new ExponentialBackoff(new BackoffOptions(), new SeededRandom(7));
        var values = Enumerable.Range(0, 20).Select(_ => b.GetDelay(3)).Distinct().Count();
        Assert.True(values > 10);
    }

    [Fact]
    public void RateLimiter_SlidingWindow()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero));
        var l = new ConnectionRateLimiter(3, TimeSpan.FromMinutes(10), clock);
        Assert.True(l.TryAcquire(out _));
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.True(l.TryAcquire(out _));
        Assert.True(l.TryAcquire(out _));
        Assert.False(l.TryAcquire(out var wait));
        Assert.Equal(TimeSpan.FromMinutes(6), wait);
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.True(l.TryAcquire(out _));
        Assert.False(l.TryAcquire(out wait));
        Assert.Equal(TimeSpan.FromMinutes(4), wait);
        Assert.Equal(100, ConnectionRateLimiter.ForKraken(clock).MaxConnections);
    }
}

public class SymbolHelperTests
{
    [Theory]
    [InlineData("XXBT", "BTC")]
    [InlineData("XBT", "BTC")]
    [InlineData("ZEUR", "EUR")]
    [InlineData("XETH", "ETH")]
    [InlineData("XDG", "DOGE")]
    [InlineData("USDC", "USDC")]
    [InlineData("SOL", "SOL")]
    public void NormalizeKrakenAsset(string raw, string expected) => Assert.Equal(expected, SymbolHelper.NormalizeKrakenAsset(raw));

    [Fact]
    public void Mapping()
    {
        Assert.Equal("BTC/EUR", SymbolHelper.FromDashed("btc-eur"));
        Assert.Equal("BTC-EUR", SymbolHelper.ToDashed("BTC/EUR"));
        Assert.Equal("XBTEUR", SymbolHelper.ToKrakenRestPair("BTC/EUR"));
        Assert.Equal(AssetClass.Crypto, SymbolHelper.AssetClassOf("SOL/EUR"));
        Assert.Equal(AssetClass.Stock, SymbolHelper.AssetClassOf("DE0007164600"));
        Assert.Null(SymbolHelper.AssetClassOf("hallo"));
    }
}
