using Musterdepot.Core.Domain;
using Musterdepot.Core.Events;
using Musterdepot.Core.MarketData;
using Musterdepot.IntegrationTests.Infrastructure;
using Musterdepot.Recorder.Fixtures;

namespace Musterdepot.IntegrationTests;

/// <summary>
/// Golden Replays: Aufzeichnung + Orderskript durch die echte Engine → fester Event-Log
/// (<c>tests/data/golden/replays/&lt;szenario&gt;.json</c>, aktualisieren mit <c>UPDATE_GOLDEN=1</c>).
/// </summary>
public class GoldenReplayTests
{
    public static TheoryData<string> Names() => ReplayFixture.Names();

    [Theory, MemberData(nameof(Names))]
    public void Replay_MatchesGolden(string name)
    {
        var fixture = ReplayFixture.Load(name);
        var (golden, _) = GoldenReplay.Run(fixture);
        GoldenFile.Default.Verify($"replays/{name}", golden);
    }

    [Theory, MemberData(nameof(Names))]
    public void Replay_FulfilsScriptExpectations(string name)
    {
        var fixture = ReplayFixture.Load(name);
        var (golden, _) = GoldenReplay.Run(fixture);
        var problems = GoldenReplay.CheckExpectations(fixture.Script, golden.Results, golden.Orders, golden.Fills);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Theory, MemberData(nameof(Names))]
    public void Replay_IsDeterministic(string name)
    {
        var fixture = ReplayFixture.Load(name);
        var a = GoldenFile.Serialize(GoldenReplay.Run(fixture).Golden);
        var b = GoldenFile.Serialize(GoldenReplay.Run(fixture).Golden);
        Assert.Equal(a, b);
    }

    [Theory, MemberData(nameof(Names))]
    public void Replay_MoneyIsConserved(string name)
    {
        var (golden, raw) = GoldenReplay.Run(ReplayFixture.Load(name));
        var p = golden.Portfolio;
        var account = raw.Engine.GetAccounts().Single();
        // Cash = Summe aller Kontobuchungen.
        Assert.Equal(raw.Engine.GetCashEntries(account.AccountId).Sum(c => c.Amount), p.Cash);
        // Cash = Startkapital − Käufe + Verkäufe − Gebühren (Fills in EUR, FxRate 1).
        var flows = golden.Fills.Sum(f => (f.Side == OrderSide.Buy ? -1 : 1) * f.Quantity * f.Price * f.FxRate - f.Fee);
        Assert.Equal(account.InitialCash + flows, p.Cash, 2);
        Assert.Equal(golden.Fills.Sum(f => f.Fee), p.FeesPaid, 2);
        Assert.All(p.Positions, pos => Assert.True(pos.Quantity >= 0));
    }

    [Fact]
    public void Gap_StopIsFilledDuringGap_AndAfterJumpBelowStop()
    {
        var fixture = ReplayFixture.Load(FixtureGenerator.GapName);
        var (golden, _) = GoldenReplay.Run(fixture);
        var gap = fixture.Events.OfType<GapDetected>().Single();

        var stop1 = golden.Orders.Single(o => o.ClientOrderId == "g-stop-1");
        var fill1 = Assert.Single(golden.Fills, f => f.OrderId == stop1.OrderId);
        Assert.True(fill1.ExecutedDuringGap);
        Assert.InRange(fill1.MarketDataAsOfUtc, gap.GapStartUtc, gap.GapEndUtc);
        Assert.True(fill1.Price <= stop1.StopPrice);

        var stop2 = golden.Orders.Single(o => o.ClientOrderId == "g-stop-2");
        var fills2 = golden.Fills.Where(f => f.OrderId == stop2.OrderId).ToList();
        Assert.NotEmpty(fills2);
        Assert.All(fills2, f => Assert.False(f.ExecutedDuringGap));
        Assert.All(fills2, f => Assert.True(f.Price < stop2.StopPrice - 200m, $"Fill {f.Price} zu nah am Stop {stop2.StopPrice}"));

        var cancelled = golden.Orders.Single(o => o.ClientOrderId == "g-lmt-cancel");
        Assert.Equal(OrderStatus.Cancelled, cancelled.Status);
        Assert.DoesNotContain(golden.Fills, f => f.OrderId == cancelled.OrderId);
    }

    [Fact]
    public void Delayed_NoFillBeforeOrderEntry()
    {
        var fixture = ReplayFixture.Load(FixtureGenerator.DelayedName);
        var (golden, _) = GoldenReplay.Run(fixture);
        var sapOrders = golden.Orders.Where(o => o.Symbol == FixtureGenerator.Sap.Symbol).ToList();
        Assert.NotEmpty(golden.Fills);
        foreach (var f in golden.Fills)
        {
            var order = sapOrders.Single(o => o.OrderId == f.OrderId);
            Assert.True(f.MarketDataAsOfUtc > order.CreatedUtc,
                $"{order.ClientOrderId}: Fill gegen Daten von {f.MarketDataAsOfUtc:O}, Ordereingang {order.CreatedUtc:O}");
            Assert.True(f.TimestampUtc >= order.CreatedUtc);
            Assert.Equal(FillQuality.Synthetic, f.Quality);
        }
        // Market-Kauf um 08:40 wurde nicht sofort gefüllt, sondern erst, als neue Daten eintrafen.
        var buy = sapOrders.Single(o => o.ClientOrderId == "s-mkt-buy-1");
        Assert.Equal(OrderStatus.Filled, buy.Status);
        Assert.True(golden.Fills.First(f => f.OrderId == buy.OrderId).TimestampUtc >= buy.CreatedUtc.AddMinutes(15));
    }
}
