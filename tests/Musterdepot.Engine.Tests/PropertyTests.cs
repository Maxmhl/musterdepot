using FsCheck.Xunit;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;
using Musterdepot.Core.Events;
using Musterdepot.Core.MarketData;
using Musterdepot.Engine.Calendar;
using static Musterdepot.Engine.Tests.TestEngine;

namespace Musterdepot.Engine.Tests;

/// <summary>
/// Eigenschaftstests: FsCheck erzeugt (und verkleinert) Folgen von Aktionen – Marktdaten, Orders aller Typen,
/// Stornos, Zeitsprünge, Einzahlungen, Lücken, Not-Aus. Nach jeder Aktion werden die Invarianten geprüft.
/// </summary>
public class PropertyTests
{
    private static readonly RiskConfig Risk = new()
    {
        MaxOrderNotional = 4_000m,
        MaxOrderPercentOfEquity = 40m,
        MaxPositionNotional = 8_000m,
        MaxPositionPercentOfEquity = 80m,
        MaxDailyLoss = 300m,
        PriceBandPercent = 5m,
        MaxOrdersPerMinuteHuman = 6,
        MaxOrdersPerMinuteAi = 3,
        MaxOpenOrders = 8,
        AllowedSymbols = [Btc, Sap, "ETH/EUR"],
    };

    private static readonly FeeModelConfig Fees = new() { Kind = FeeModelKind.Combined, FixedFee = 0.3m, MakerRate = 0.001m, TakerRate = 0.003m, MinFee = 0.5m, MaxFee = 20m };

    private sealed class Sim(int seed)
    {
        public readonly TestEngine T = new(risk: Risk, cryptoFees: Fees, stockFees: FeeModelConfig.NeoBroker, initialCash: 10_000m);
        public readonly Random R = new(seed);
        public decimal Btc = 100m, Sap = 50m;

        public decimal Px(decimal around, decimal spreadPct) => Math.Round(around * (1 + (decimal)(R.NextDouble() * 2 - 1) * spreadPct / 100m), 2);
        public decimal Qty() => Math.Round((decimal)R.NextDouble() * 20m + 0.01m, 2);
        public OrderSide Side() => R.Next(2) == 0 ? OrderSide.Buy : OrderSide.Sell;
        public CommandSource Src() => R.Next(3) == 0 ? CommandSource.Ai : CommandSource.Human;
        public TimeInForce Tif() => (TimeInForce)R.Next(5) is var t && t == TimeInForce.Gtd ? TimeInForce.Gtc : t;

        public void Step(int op)
        {
            var t = T;
            switch (Math.Abs(op) % 15)
            {
                case 0 or 1:
                    Btc = Math.Max(1m, Px(Btc, 3m));
                    t.Trade(TestEngine.Btc, Btc, qty: Qty(), aggressor: R.Next(3) == 0 ? Side() : null);
                    break;
                case 2:
                    t.Book(TestEngine.Btc, Enumerable.Range(1, 4).Select(i => (Btc - i * 0.1m, Qty())).ToArray(), Enumerable.Range(1, 4).Select(i => (Btc + i * 0.1m, Qty())).ToArray());
                    break;
                case 3:
                    t.Book(TestEngine.Btc, [(Btc - 0.1m, R.Next(3) == 0 ? 0m : Qty())], [(Btc + 0.1m, R.Next(3) == 0 ? 0m : Qty())], snapshot: false);
                    break;
                case 4:
                    t.Place(TestEngine.Btc, Side(), OrderType.Market, Qty(), tif: Tif(), source: Src(), tag: R.Next(2) == 0 ? "a" : "b");
                    break;
                case 5:
                    t.Place(TestEngine.Btc, Side(), OrderType.Limit, Qty(), limit: Px(Btc, 4m), tif: Tif(), source: Src(), oco: R.Next(4) == 0 ? "g" : null);
                    break;
                case 6:
                    {
                        var side = Side();
                        var stop = Px(Btc, 4m);
                        if (R.Next(2) == 0) t.Place(TestEngine.Btc, side, OrderType.Stop, Qty(), stop: stop, tif: Tif(), source: Src(), oco: R.Next(4) == 0 ? "g" : null);
                        else t.Place(TestEngine.Btc, side, OrderType.StopLimit, Qty(), stop: stop, limit: side == OrderSide.Buy ? stop + 0.5m : stop - 0.5m, tif: Tif(), source: Src());
                        break;
                    }
                case 7:
                    if (R.Next(2) == 0) t.Place(TestEngine.Btc, OrderSide.Sell, OrderType.TrailingStop, Qty(), trailPercent: 1m + R.Next(3), source: Src());
                    else t.Place(TestEngine.Btc, OrderSide.Buy, OrderType.Limit, Qty(), limit: Px(Btc, 1m), tp: Math.Round(Btc * 1.03m, 2), sl: Math.Round(Btc * 0.97m, 2), source: Src(), tag: "br");
                    break;
                case 8:
                    {
                        var open = t.Engine.GetOrders(Acc, openOnly: true);
                        if (open.Count > 0) t.Engine.Execute(new CancelOrderRequest { AccountId = Acc, OrderId = open[R.Next(open.Count)].OrderId, Source = Src() });
                        break;
                    }
                case 9:
                    t.AdvanceAndTick(TimeSpan.FromMilliseconds(R.Next(5) switch { 0 => 50, 1 => 250, 2 => 20_000, 3 => 600_000, _ => 7_200_000 }));
                    break;
                case 10:
                    t.Engine.Execute(new DepositCashRequest { AccountId = Acc, Amount = R.Next(1, 2000) });
                    break;
                case 11:
                    Sap = Math.Max(1m, Px(Sap, 2m));
                    t.Trade(TestEngine.Sap, Sap, qty: R.Next(1, 300), delayedMinutes: 15, asOf: t.Now.AddMinutes(-R.Next(0, 30)));
                    if (R.Next(2) == 0) t.Place(TestEngine.Sap, Side(), R.Next(2) == 0 ? OrderType.Market : OrderType.Limit, R.Next(1, 40), limit: Px(Sap, 2m), source: Src());
                    break;
                case 12:
                    {
                        var start = t.Now;
                        t.Advance(600_000);
                        var candles = new List<Candle>();
                        var o = Btc;
                        for (int i = 0; i < 5; i++)
                        {
                            var c = Math.Max(1m, Px(o, 4m));
                            candles.Add(new Candle
                            {
                                Symbol = TestEngine.Btc, Timeframe = Timeframe.M1, OpenTimeUtc = start.AddMinutes(i + 1), Open = o, Close = c,
                                High = Math.Max(o, c) + 0.3m, Low = Math.Max(0.5m, Math.Min(o, c) - 0.3m), Volume = Qty(), IsBackfill = true,
                            });
                            o = Math.Max(1m, Px(c, 1m));
                        }
                        Btc = candles[^1].Close;
                        t.Engine.OnMarketEvent(new GapDetected { Symbol = TestEngine.Btc, Source = "kraken", AsOfUtc = t.Now, GapStartUtc = start, GapEndUtc = t.Now, BackfilledCandles = candles });
                        break;
                    }
                case 13:
                    if (t.Engine.IsKillSwitchEngaged(Acc))
                        t.Engine.Execute(new ReleaseKillSwitchRequest { AccountId = Acc, ReleasedBy = "x", Source = Src() });
                    else if (R.Next(3) == 0)
                        t.Engine.Execute(new EngageKillSwitchRequest { AccountId = Acc, Reason = "prop", Source = Src() });
                    break;
                case 14:
                    t.Quote(TestEngine.Btc, Btc - 0.05m, Btc + 0.05m);
                    t.Place("ETH/EUR", Side(), OrderType.Limit, Qty(), limit: 10m, source: Src()); // nie ein Kurs → abgelehnt
                    t.Place("DOGE/EUR", OrderSide.Buy, OrderType.Limit, Qty(), limit: 1m);      // nicht erlaubt
                    break;
            }
        }
    }

    private static void CheckInvariants(Sim sim)
    {
        var t = sim.T;
        var s = t.State;
        // keine negativen Mengen/Cash
        Assert.True(s.Cash >= 0, $"Cash negativ: {s.Cash}");
        Assert.All(s.Lots.Values.SelectMany(l => l), l => { Assert.True(l.Quantity > 0); Assert.True(l.Cost >= 0); Assert.True(l.Fee >= 0); });
        Assert.All(s.Orders.Values, o => { Assert.True(o.FilledQuantity >= 0 && o.FilledQuantity <= o.Quantity, $"{o.OrderId}: {o.FilledQuantity}/{o.Quantity}"); });
        // Geld bleibt erhalten
        Assert.Equal(s.InitialCash + s.TotalDeposits - s.TotalWithdrawals - s.FeesPaid + s.RealizedPnlGross, s.Cash + s.OpenCost);
        Assert.Equal(s.InitialCash + s.TotalDeposits - s.TotalWithdrawals + s.RealizedPnl, s.Cash + s.OpenCostInclFees);
        Assert.Equal(s.CashEntries.Sum(c => c.Amount), s.Cash);
        // Equity = Cash + Positionswert nach jeder Buchung
        var pf = t.Portfolio;
        Assert.Equal(pf.Cash + pf.PositionsValue, pf.Equity);
        Assert.Equal(pf.PositionsValue, pf.Positions.Sum(p => p.MarketValue ?? p.CostBasis));
        Assert.All(s.EquityCurve, p => Assert.Equal(p.Cash + p.PositionsValue, p.Equity));
        if (s.EquityCurve.Count > 0) Assert.Equal(s.Cash, s.EquityCurve[^1].Cash);
        // Limit-Fills nie schlechter als Limit
        foreach (var f in s.Fills)
        {
            var o = s.Orders[f.OrderId];
            if (o.Type is OrderType.Limit or OrderType.StopLimit && o.LimitPrice is { } lim)
                Assert.True(f.Side == OrderSide.Buy ? f.Price <= lim : f.Price >= lim, $"Limit-Fill {f.Price} schlechter als {lim}");
            Assert.True(f.Quantity > 0 && f.Price > 0 && f.Fee >= 0);
        }
    }

    /// <summary>Risiko-Gate nie umgangen: nachträglich aus dem Event-Log geprüft.</summary>
    private static void CheckRiskGateFromLog(IReadOnlyList<DomainEvent> events)
    {
        var kill = false;
        DateOnly? lossDay = null;
        var risk = Risk;
        var open = new Dictionary<string, Order>();
        var submissions = new List<(DateTimeOffset T, CommandSource S)>();
        var position = new Dictionary<string, decimal>();
        foreach (var e in events)
        {
            switch (e)
            {
                case AccountOpened ao: risk = ao.Risk; break;
                case RiskConfigChanged rc: risk = rc.Risk; break;
                case KillSwitchEngaged: kill = true; break;
                case KillSwitchReleased: kill = false; break;
                case DailyLossLimitHit: lossDay = BerlinTime.TradingDay(e.TimestampUtc); break;
                case OrderSubmitted os:
                    var o = os.Order;
                    Assert.False(kill, "Order trotz Not-Aus angenommen");
                    Assert.Contains(o.Symbol, risk.AllowedSymbols!);
                    if (o.ParentOrderId is null)
                    {
                        if (o.Side == OrderSide.Buy) Assert.False(lossDay == BerlinTime.TradingDay(e.TimestampUtc), "Kauf nach Tagesverlustgrenze");
                        submissions.Add((e.TimestampUtc, o.Source));
                        var limit = o.Source == CommandSource.Ai ? risk.MaxOrdersPerMinuteAi : risk.MaxOrdersPerMinuteHuman;
                        Assert.True(submissions.Count(x => x.S == o.Source && x.T > e.TimestampUtc - TimeSpan.FromMinutes(1)) <= limit, "Ratenlimit überschritten");
                        if (o.Side == OrderSide.Buy && o.LimitPrice is { } lp && o.Type == OrderType.Limit)
                            Assert.True(o.Quantity * lp <= risk.MaxOrderNotional, "MaxOrderNotional überschritten");
                    }
                    open[o.OrderId] = o;
                    Assert.True(open.Count <= risk.MaxOpenOrders, "MaxOpenOrders überschritten");
                    break;
                case OrderFilled f:
                    var sign = f.Fill.Side == OrderSide.Buy ? 1 : -1;
                    position[f.Fill.Symbol] = (position.TryGetValue(f.Fill.Symbol, out var p) ? p : 0) + sign * f.Fill.Quantity;
                    Assert.True(position[f.Fill.Symbol] >= 0, "Leerverkauf");
                    if (f.ResultingStatus == OrderStatus.Filled) open.Remove(f.Fill.OrderId);
                    break;
                case OrderCancelled c: open.Remove(c.OrderId); break;
                case OrderExpired x: open.Remove(x.OrderId); break;
                case OrderModified m when m.NewQuantity is { } nq && open.TryGetValue(m.OrderId, out var om):
                    open[m.OrderId] = om with { Quantity = nq };
                    break;
            }
        }
    }

    [Property(MaxTest = 150)]
    public void Invariants_hold_for_random_action_sequences(int seed, int[] ops)
    {
        var sim = new Sim(seed);
        sim.T.Trade(Btc, sim.Btc);
        sim.T.Trade(Sap, sim.Sap, delayedMinutes: 15, asOf: sim.T.Now.AddMinutes(-15));
        foreach (var op in ops.Concat(Enumerable.Range(0, 40).Select(i => seed * 31 + i * 7)))
        {
            sim.Step(op);
            CheckInvariants(sim);
        }
        var events = sim.T.Engine.GetEvents(Acc);
        CheckRiskGateFromLog(events);
        // Ereignisse ergeben exakt denselben Zustand
        var restored = new TradingEngine(new ManualClock(sim.T.Now), new SequentialIdGenerator());
        restored.LoadFromEvents(events);
        Assert.Equal(ReplayTests.Fingerprint(sim.T.State), ReplayTests.Fingerprint(restored.GetAccountState(Acc)!));
    }

    [Fact]
    public void Random_sequences_exercise_the_engine()
    {
        int fills = 0, rejects = 0, gapFills = 0, synthetic = 0, book = 0, sells = 0, cancels = 0;
        for (int seed = 0; seed < 40; seed++)
        {
            var sim = new Sim(seed);
            sim.T.Trade(Btc, sim.Btc);
            sim.T.Trade(Sap, sim.Sap, delayedMinutes: 15, asOf: sim.T.Now.AddMinutes(-15));
            for (int i = 0; i < 150; i++) { sim.Step(sim.R.Next()); CheckInvariants(sim); }
            var s = sim.T.State;
            fills += s.Fills.Count; rejects += s.Rejections.Count; gapFills += s.Fills.Count(f => f.ExecutedDuringGap);
            synthetic += s.Fills.Count(f => f.Quality == FillQuality.Synthetic); book += s.Fills.Count(f => f.Quality == FillQuality.OrderBook);
            sells += s.Fills.Count(f => f.Side == OrderSide.Sell); cancels += s.Orders.Values.Count(o => o.Status == OrderStatus.Cancelled);
            CheckRiskGateFromLog(sim.T.Engine.GetEvents(Acc));
        }
        Assert.True(fills > 100 && rejects > 50 && gapFills > 0 && synthetic > 0 && book > 0 && sells > 10 && cancels > 10,
            $"fills={fills} rejects={rejects} gap={gapFills} synthetic={synthetic} book={book} sells={sells} cancels={cancels}");
    }

    [Property(MaxTest = 200)]
    public void Rejected_orders_never_create_orders(int seed)
    {
        var sim = new Sim(seed);
        sim.T.Trade(Btc, 100m);
        for (int i = 0; i < 30; i++)
        {
            var before = sim.T.Engine.GetOrders(Acc).Count;
            var r = sim.T.Place(Btc, sim.Side(), OrderType.Limit, sim.Qty() * 10, limit: sim.Px(100m, 10m), source: sim.Src());
            var after = sim.T.Engine.GetOrders(Acc).Count;
            if (r.Accepted) Assert.Equal(before + 1, after);
            else
            {
                Assert.Equal(before, after);
                Assert.Single(r.Events.OfType<OrderRejected>());
                Assert.DoesNotContain(r.Events, e => e is OrderSubmitted);
            }
        }
    }
}
