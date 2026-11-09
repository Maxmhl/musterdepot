using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;
using Musterdepot.Core.Serialization;
using Musterdepot.IntegrationTests.Infrastructure;
using Musterdepot.Recorder.Fixtures;
using Musterdepot.Recorder.Recording;

namespace Musterdepot.IntegrationTests;

/// <summary>Die eingecheckten Replay-Aufzeichnungen sind lesbar, sortiert, stimmig und überstehen den JSONL-Rundlauf.</summary>
public class FixtureTests
{
    public static TheoryData<string> Names() => ReplayFixture.Names();

    [Fact]
    public void AtLeastThreeScenariosExist()
    {
        var names = ReplayFixture.AllNames();
        Assert.Contains(FixtureGenerator.QuietName, names);
        Assert.Contains(FixtureGenerator.GapName, names);
        Assert.Contains(FixtureGenerator.DelayedName, names);
    }

    [Theory, MemberData(nameof(Names))]
    public void Fixture_IsReadable(string name)
    {
        var f = ReplayFixture.Load(name);
        Assert.NotEmpty(f.Events);
        Assert.All(f.Events, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Symbol));
            Assert.False(string.IsNullOrWhiteSpace(e.Source));
            Assert.Equal(TimeSpan.Zero, e.AsOfUtc.Offset);
            Assert.NotEqual(default, e.ReceivedUtc);
            Assert.True(e.ReceivedUtc >= e.AsOfUtc, $"ReceivedUtc vor AsOfUtc: {e}");
        });
    }

    [Theory, MemberData(nameof(Names))]
    public void Fixture_IsSortedByReplayTime_AndAsOfPerSymbol(string name)
    {
        var f = ReplayFixture.Load(name);
        for (int i = 1; i < f.Events.Count; i++)
            Assert.True(ReplayClock.TimeOf(f.Events[i]) >= ReplayClock.TimeOf(f.Events[i - 1]), $"Zeile {i + 1}: ReceivedUtc springt zurück.");
        foreach (var group in f.Events.Where(e => e.Symbol != "*").GroupBy(e => e.Symbol))
        {
            var list = group.ToList();
            for (int i = 1; i < list.Count; i++)
                Assert.True(list[i].AsOfUtc >= list[i - 1].AsOfUtc, $"{group.Key}: AsOfUtc springt zurück bei {list[i].AsOfUtc:O}.");
        }
    }

    [Theory, MemberData(nameof(Names))]
    public void Fixture_LinesAreCanonical(string name)
    {
        // Jede Zeile entspricht exakt der Serialisierung des gelesenen Ereignisses (kein Informationsverlust).
        var f = ReplayFixture.Load(name);
        var lines = File.ReadAllLines(f.ReplayPath).Where(l => l.Length > 0).ToList();
        Assert.Equal(f.Events.Count, lines.Count);
        for (int i = 0; i < lines.Count; i++)
            Assert.Equal(lines[i], JsonDefaults.SerializeMarketEvent(f.Events[i]));
    }

    [Theory, MemberData(nameof(Names))]
    public async Task Fixture_RoundTripsThroughJsonlAndGzipAndWriter(string name)
    {
        var f = ReplayFixture.Load(name);
        var dir = TestPaths.NewTempDirectory();
        try
        {
            var expected = f.Events.Select(JsonDefaults.SerializeMarketEvent).ToList();

            var plain = Path.Combine(dir, "plain.jsonl");
            await MarketEventJsonl.WriteAsync(plain, f.Events);
            Assert.Equal(expected, MarketEventJsonl.ReadAll(plain).Select(JsonDefaults.SerializeMarketEvent));

            var gz = Path.Combine(dir, "packed.jsonl.gz");
            await MarketEventJsonl.WriteAsync(gz, f.Events);
            var streamed = new List<string>();
            await foreach (var e in MarketEventJsonl.ReadAsync(gz)) streamed.Add(JsonDefaults.SerializeMarketEvent(e));
            Assert.Equal(expected, streamed);

            var appended = Path.Combine(dir, "appended.jsonl.gz");
            await using (var w = new MarketEventJsonl.Writer(appended))
                foreach (var e in f.Events) await w.WriteAsync(e);
            Assert.Equal(expected, MarketEventJsonl.ReadAll(appended).Select(JsonDefaults.SerializeMarketEvent));
        }
        finally
        {
            TestPaths.TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public void Quiet_ContainsAllEventKinds_ForAbout30Minutes()
    {
        var f = ReplayFixture.Load(FixtureGenerator.QuietName);
        var kinds = f.Events.GroupBy(MarketEventKinds.Of).ToDictionary(g => g.Key, g => g.Count());
        foreach (var k in new[] { "book-snapshot", "book-delta", "trade", "quote", "candle", "status" })
            Assert.True(kinds.GetValueOrDefault(k) > 0, $"Ereignistyp {k} fehlt.");
        var marketEvents = f.Events.Where(e => e.Symbol == "BTC/EUR").ToList();
        var span = marketEvents[^1].AsOfUtc - marketEvents[0].AsOfUtc;
        Assert.InRange(span.TotalMinutes, 29, 31);
        Assert.Equal(30, f.Events.OfType<CandleUpdate>().Count(c => c.Candle.Timeframe == Timeframe.M1));
        Assert.All(f.Events, e => Assert.Equal(DataQuality.Realtime, e.Quality));
    }

    [Theory]
    [InlineData(FixtureGenerator.QuietName)]
    [InlineData(FixtureGenerator.GapName)]
    public void CryptoFixtures_BookIsNeverCrossed_AndQuotesMatchBook(string name)
    {
        var f = ReplayFixture.Load(name);
        var book = new OrderBookState();
        int quotes = 0;
        foreach (var e in f.Events)
        {
            switch (e)
            {
                case OrderBookUpdate u:
                    Assert.True(u.IsSnapshot || book.HasSnapshot, "Delta vor dem ersten Snapshot.");
                    book.Apply(u);
                    Assert.False(book.IsCrossed, $"Buch gekreuzt bei {u.AsOfUtc:O}: {book.BestBid} >= {book.BestAsk}");
                    break;
                case QuoteTick q:
                    Assert.Equal(book.BestBid, q.Bid);
                    Assert.Equal(book.BestAsk, q.Ask);
                    Assert.Equal(book.BestBidSize, q.BidSize);
                    Assert.Equal(book.BestAskSize, q.AskSize);
                    quotes++;
                    break;
                case TradeTick t:
                    // Trades laufen gegen die beste Gegenseite des aktuellen Buchs.
                    var expected = t.AggressorSide == OrderSide.Buy ? book.BestAsk : book.BestBid;
                    Assert.Equal(expected, t.Price);
                    break;
            }
        }
        Assert.True(quotes > 100);
    }

    [Theory, MemberData(nameof(Names))]
    public void LiveCandles_MatchTheirTrades(string name)
    {
        var f = ReplayFixture.Load(name);
        var trades = f.Events.OfType<TradeTick>().ToList();
        foreach (var cu in f.Events.OfType<CandleUpdate>())
        {
            var c = cu.Candle;
            Assert.False(c.IsBackfill);
            var inMinute = trades.Where(t => t.Symbol == c.Symbol && t.AsOfUtc >= c.OpenTimeUtc && t.AsOfUtc < c.OpenTimeUtc.AddMinutes(1)).ToList();
            Assert.Equal(inMinute.Count, c.TradeCount);
            Assert.Equal(inMinute.Sum(t => t.Quantity), c.Volume);
            Assert.True(cu.AsOfUtc >= c.OpenTimeUtc.AddMinutes(1), "Kerze vor Ende ihrer Minute gemeldet.");
            if (inMinute.Count == 0)
            {
                Assert.True(c.Open == c.High && c.High == c.Low && c.Low == c.Close, "Kerze ohne Trades muss flach sein.");
                continue;
            }
            Assert.Equal(inMinute[0].Price, c.Open);
            Assert.Equal(inMinute[^1].Price, c.Close);
            Assert.Equal(inMinute.Max(t => t.Price), c.High);
            Assert.Equal(inMinute.Min(t => t.Price), c.Low);
        }
    }

    [Fact]
    public void Gap_HasGapDetectedWithConsecutiveBackfilledCandles()
    {
        var f = ReplayFixture.Load(FixtureGenerator.GapName);
        var gap = Assert.Single(f.Events.OfType<GapDetected>());
        Assert.True(gap.GapEndUtc > gap.GapStartUtc);
        Assert.True(gap.AsOfUtc >= gap.GapEndUtc);
        Assert.Equal(7, gap.BackfilledCandles.Count);
        for (int i = 0; i < gap.BackfilledCandles.Count; i++)
        {
            var c = gap.BackfilledCandles[i];
            Assert.True(c.IsBackfill);
            Assert.Equal(Timeframe.M1, c.Timeframe);
            Assert.Equal(gap.GapStartUtc.AddMinutes(i), c.OpenTimeUtc);
            Assert.True(c.Low <= Math.Min(c.Open, c.Close) && c.High >= Math.Max(c.Open, c.Close));
        }
        Assert.Equal(gap.GapEndUtc, gap.BackfilledCandles[^1].OpenTimeUtc.AddMinutes(1));

        // Keine Live-Daten während der Lücke, Status Suspended → Reconnecting → Connected.
        Assert.DoesNotContain(f.Events, e => e.Symbol == "BTC/EUR" && e is not GapDetected
            && e.AsOfUtc > gap.GapStartUtc.AddSeconds(1) && e.AsOfUtc < gap.GapEndUtc);
        var states = f.Events.OfType<ConnectionStatusChanged>().Select(s => s.State).ToList();
        Assert.Equal([ConnectionState.Connected, ConnectionState.Suspended, ConnectionState.Reconnecting, ConnectionState.Connected], states);
    }

    [Fact]
    public void Gap_StopsAreReachableAsScripted()
    {
        var f = ReplayFixture.Load(FixtureGenerator.GapName);
        var gap = f.Events.OfType<GapDetected>().Single();
        var trades = f.Events.OfType<TradeTick>().ToList();
        var stop1 = Step(f, "g-stop-1");
        var stop2 = Step(f, "g-stop-2");

        // Stop 1: vor der Lücke nie erreicht, in den nachgeladenen Kerzen schon.
        Assert.All(trades.Where(t => t.AsOfUtc > stop1.AtUtc && t.AsOfUtc < gap.GapStartUtc), t => Assert.True(t.Price > stop1.PlaceOrder!.StopPrice));
        Assert.Contains(gap.BackfilledCandles, c => c.Low <= stop1.PlaceOrder!.StopPrice);

        // Stop 2: bis zum Sprung nicht erreicht; erster Trade danach mindestens 200 EUR unter dem Stop.
        var firstBelow = trades.First(t => t.AsOfUtc > stop2.AtUtc && t.Price <= stop2.PlaceOrder!.StopPrice);
        Assert.True(firstBelow.Price <= stop2.Expect!.MaxFillPrice, $"Sprung zu klein: {firstBelow.Price}");
        var previous = trades.Last(t => t.AsOfUtc < firstBelow.AsOfUtc);
        Assert.True(previous.Price - firstBelow.Price > 300m, "Kein Kurssprung zwischen zwei Trades.");

        // Die stornierte Limit-Order wäre in der Lücke gefüllt worden – Stornierung ist also relevant.
        var cancelled = Step(f, "g-lmt-cancel");
        Assert.Contains(gap.BackfilledCandles, c => c.Low <= cancelled.PlaceOrder!.LimitPrice);
    }

    [Fact]
    public void Delayed_IsDelayedStockFromGettex_ArrivingInQuarterHourFiles()
    {
        var f = ReplayFixture.Load(FixtureGenerator.DelayedName);
        Assert.All(f.Events, e =>
        {
            Assert.Equal("gettex", e.Source);
            Assert.Equal(15, e.DelayedMinutes);
            Assert.Equal(DataQuality.Delayed, e.Quality);
            Assert.False(e.IsRealtime);
        });
        var market = f.Events.Where(e => e.Symbol != "*").ToList();
        Assert.All(market, e => Assert.Equal("DE0007164600", e.Symbol));
        Assert.All(market, e => Assert.True(e.ReceivedUtc - e.AsOfUtc >= TimeSpan.FromMinutes(15), $"Zu früh empfangen: {e.AsOfUtc:O} → {e.ReceivedUtc:O}"));
        Assert.Empty(f.Events.OfType<OrderBookUpdate>());
        Assert.All(f.Events.OfType<QuoteTick>(), q => Assert.Null(q.Bid)); // kein Bid/Ask → angenommener Spread
        var span = market.Max(e => e.AsOfUtc) - market.Min(e => e.AsOfUtc);
        Assert.InRange(span.TotalHours, 1.9, 2.1);
        Assert.InRange(market.Select(e => e.ReceivedUtc).Distinct().Count(), 7, 9); // eine Datei je Viertelstunde
    }

    [Fact]
    public void Delayed_MarketOrdersHaveTradesAfterOrderTime()
    {
        var f = ReplayFixture.Load(FixtureGenerator.DelayedName);
        var trades = f.Events.OfType<TradeTick>().ToList();
        foreach (var step in f.Script.Steps.Where(s => s.Expect?.FillsOnlyFromMarketDataAfterUtc is not null))
        {
            var after = step.Expect!.FillsOnlyFromMarketDataAfterUtc!.Value;
            // Zum Ordereingang ist noch kein Trade nach dem Ordereingang sichtbar …
            Assert.DoesNotContain(trades, t => t.AsOfUtc > after && t.ReceivedUtc <= step.AtUtc);
            // … aber später in der Aufzeichnung.
            Assert.Contains(trades, t => t.AsOfUtc > after);
        }
    }

    private static OrderScriptStep Step(ReplayFixture f, string clientOrderId) =>
        f.Script.Steps.Single(s => s.PlaceOrder?.ClientOrderId == clientOrderId);
}
