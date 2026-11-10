using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;
using Musterdepot.Core.Serialization;
using Musterdepot.MarketData.Replay;

namespace Musterdepot.MarketData.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "md-tests-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}

public class JsonlTests
{
    internal static List<MarketEvent> SampleEvents()
    {
        var t0 = new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);
        return
        [
            new TradeTick { Symbol = "BTC/EUR", Source = "kraken", AsOfUtc = t0.AddSeconds(2), Price = 62000.10m, Quantity = 0.00012345m, AggressorSide = OrderSide.Buy, TradeId = "1" },
            new QuoteTick { Symbol = "BTC/EUR", Source = "kraken", AsOfUtc = t0.AddSeconds(1), Bid = 61999.9m, Ask = 62000.0m, Last = 62000.0m },
            new OrderBookUpdate { Symbol = "BTC/EUR", Source = "kraken", AsOfUtc = t0, IsSnapshot = true, Bids = [new(61999.9m, 1.5m)], Asks = [new(62000.0m, 0.25m)], Checksum = 123 },
            new TradeTick { Symbol = "ETH/EUR", Source = "kraken", AsOfUtc = t0.AddSeconds(2), Price = 3100.01m, Quantity = 1m },
            new TradeTick { Symbol = "BTC/EUR", Source = "kraken", AsOfUtc = t0.AddSeconds(65), Price = 62010m, Quantity = 0.5m },
            new ConnectionStatusChanged { Symbol = "*", Source = "kraken", AsOfUtc = t0.AddSeconds(3), State = ConnectionState.Reconnecting },
            new CandleUpdate { Symbol = "BTC/EUR", Source = "kraken", AsOfUtc = t0.AddSeconds(60), Candle = new Candle { Symbol = "BTC/EUR", Timeframe = Timeframe.M1, OpenTimeUtc = t0, Open = 62000m, High = 62000.1m, Low = 61999m, Close = 62000.1m, Volume = 2m } },
        ];
    }

    [Theory]
    [InlineData("rec.jsonl")]
    [InlineData("rec.jsonl.gz")]
    public async Task RoundTrip_PreservesDecimalsAndTypes(string name)
    {
        using var dir = new TempDir();
        var events = SampleEvents();
        await MarketEventJsonl.WriteAsync(dir.File(name), events);
        var back = MarketEventJsonl.ReadAll(dir.File(name));
        Assert.Equal(events.Count, back.Count);
        for (int i = 0; i < events.Count; i++)
        {
            Assert.Equal(events[i].GetType(), back[i].GetType());
            Assert.Equal(JsonDefaults.SerializeMarketEvent(events[i]), JsonDefaults.SerializeMarketEvent(back[i]));
        }
        Assert.Equal(0.00012345m, ((TradeTick)back[0]).Quantity);
    }
}

public class ReplayTests
{
    [Fact]
    public async Task Recorded_PlaysInTimeOrder_WithManualClock()
    {
        using var dir = new TempDir();
        await MarketEventJsonl.WriteAsync(dir.File("r.jsonl"), JsonlTests.SampleEvents());
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var src = new RecordedMarketDataSource(dir.File("r.jsonl"), clock, new RecordedReplayOptions { AutoPlay = false });
        var log = new EventLog();
        src.EventReceived += log.Add;
        await src.LoadAsync(default);
        var t0 = new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);
        Assert.Equal(t0, clock.UtcNow); // Uhr steht auf dem ersten Ereignis
        Assert.Equal(6, src.Count); // Statusmeldung aus der Aufzeichnung wird nicht abgespielt

        Assert.Equal(1, src.AdvanceTo(t0));
        Assert.Equal(1, src.Advance(TimeSpan.FromSeconds(1)));
        Assert.Equal(2, src.Step()); // beide Trades bei t0+2s
        Assert.Equal(t0.AddSeconds(2), clock.UtcNow);
        var market = log.All.Where(e => e is not ConnectionStatusChanged).ToList();
        Assert.Equal(market.OrderBy(e => e.AsOfUtc).Select(e => e.AsOfUtc), market.Select(e => e.AsOfUtc));
        Assert.All(market, e => Assert.Equal(e.AsOfUtc, e.ReceivedUtc));
        Assert.Equal("kraken", market[0].Source);

        // Kein Blick in die Zukunft: die Kerze/der Trade bei +60/+65 s ist noch nicht sichtbar.
        var candles = await src.GetCandlesAsync("BTC/EUR", Timeframe.M1, null, null, default);
        Assert.DoesNotContain(candles, c => c.OpenTimeUtc == t0.AddMinutes(1));
        var ob = await src.GetOrderBookAsync("BTC/EUR", 10, default);
        Assert.Equal(61999.9m, ob!.BestBid);

        Assert.Equal(2, src.RunToEnd());
        Assert.True(src.IsFinished);
        Assert.Equal(ConnectionState.Disconnected, src.State);
        Assert.Equal(t0.AddSeconds(65), clock.UtcNow);
    }

    [Fact]
    public async Task Recorded_OverrideQualityAndSource_AndSubscriptionFilter()
    {
        using var dir = new TempDir();
        await MarketEventJsonl.WriteAsync(dir.File("r.jsonl.gz"), JsonlTests.SampleEvents());
        var src = new RecordedMarketDataSource(dir.File("r.jsonl.gz"), null,
            new RecordedReplayOptions { AutoPlay = false, OverrideQuality = DataQuality.Replay, PreserveSource = false });
        var log = new EventLog();
        src.EventReceived += log.Add;
        await src.LoadAsync(default);
        await src.SubscribeAsync(["ETH/EUR"], default);
        src.RunToEnd();
        var e = Assert.Single(log.All, x => x is not ConnectionStatusChanged);
        Assert.Equal("ETH/EUR", e.Symbol);
        Assert.Equal(DataQuality.Replay, e.Quality);
        Assert.Equal("replay", e.Source);
        Assert.False(e.IsRealtime);
    }

    [Fact]
    public async Task Recorded_AutoPlay_WithSpeedFactor()
    {
        using var dir = new TempDir();
        await MarketEventJsonl.WriteAsync(dir.File("r.jsonl"), JsonlTests.SampleEvents());
        var src = new RecordedMarketDataSource(dir.File("r.jsonl"), null,
            new RecordedReplayOptions { Speed = 1000, TickInterval = TimeSpan.FromMilliseconds(5), MaxIdleGap = null });
        await src.StartAsync(default);
        await Wait.UntilAsync(() => src.IsFinished, because: "65 s bei Faktor 1000 ≈ 65 ms");
        src.Pause();
        Assert.True(src.IsPaused);
        await src.StopAsync(default);
    }

    [Fact]
    public async Task GettexReplay_PlaysDayFiles_WithReplayQuality_AndOptionalDelay()
    {
        using var dir = new TempDir();
        File.Copy(Fixture.Path("gettex/posttrade_de_semicolon.csv"), dir.File("posttrade.20261109.09.00.csv"));
        File.WriteAllBytes(dir.File("posttrade.20261109.09.15.csv.gz"), GettexParserTests.Gzip(Fixture.Read("gettex/posttrade_tab.csv")));
        File.Copy(Fixture.Path("gettex/posttrade_en_comma.csv"), dir.File("posttrade.20261108.09.00.csv")); // anderer Tag
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var src = new GettexReplaySource(new GettexReplayOptions
        {
            Directory = dir.Path, Date = new DateOnly(2026, 11, 9), AutoPlay = false, SimulatedDelayMinutes = 15,
        }, clock);
        var log = new EventLog();
        src.EventReceived += log.Add;
        await src.LoadAsync(default);
        Assert.Equal(6, src.Count);
        var first = new DateTimeOffset(2026, 11, 9, 8, 0, 1, TimeSpan.Zero).AddTicks(1_234_560);
        Assert.Equal(first.AddMinutes(15), clock.UtcNow);

        // bis 08:16 UTC: nur Trades bis 08:01 Handelszeit sind „angekommen“
        src.AdvanceTo(new DateTimeOffset(2026, 11, 9, 8, 16, 0, TimeSpan.Zero));
        var trades = log.Of<TradeTick>();
        Assert.Equal(3, trades.Count);
        Assert.All(trades, t => { Assert.Equal(DataQuality.Replay, t.Quality); Assert.Equal(15, t.DelayedMinutes); Assert.True(t.ReceivedUtc >= t.AsOfUtc.AddMinutes(15)); });
        src.RunToEnd();
        var all = log.Of<TradeTick>();
        Assert.Equal(6, all.Count);
        Assert.Equal(all.OrderBy(t => t.AsOfUtc).Select(t => t.AsOfUtc), all.Select(t => t.AsOfUtc));
        Assert.Contains(all, t => t.Symbol == "DE0007236101");
        var found = await src.SearchSymbolsAsync("Siemens", default);
        Assert.Contains(found, i => i.Symbol == "DE0007236101");
    }
}
