using Musterdepot.Core.Domain;
using Musterdepot.Core.Events;
using Musterdepot.Core.MarketData;
using static Musterdepot.Engine.Tests.TestEngine;

namespace Musterdepot.Engine.Tests;

public class DelayedStockTests
{
    [Fact]
    public void Delayed_stock_never_fills_before_order_entry_and_uses_synthetic_spread()
    {
        var t = new TestEngine();
        // Datenstand 15 Minuten alt
        t.Trade(Sap, 200m, qty: 100m, delayedMinutes: 15, asOf: t.Now.AddMinutes(-15));
        var entry = t.Now;
        var r = t.Place(Sap, OrderSide.Buy, OrderType.Market, 10m);
        Assert.True(r.Accepted, r.RejectReason);
        Assert.Equal(entry.AddMilliseconds(200), t.Order(r.OrderId!).EligibleFromUtc);

        // Weitere verzögerte Trades mit Zeitstempel VOR dem Ordereingang treffen ein → kein Fill.
        for (int i = 1; i <= 14; i++)
        {
            t.AdvanceAndTick(TimeSpan.FromMinutes(1));
            t.Trade(Sap, 150m + i, qty: 100m, delayedMinutes: 15, asOf: t.Now.AddMinutes(-15));
            Assert.Empty(t.Fills);
        }
        // Trade genau beim Eingang (vor Ablauf der Latenz) → kein Fill
        t.AdvanceAndTick(TimeSpan.FromMinutes(1));
        t.Trade(Sap, 201m, delayedMinutes: 15, asOf: entry.AddMilliseconds(100));
        Assert.Empty(t.Fills);
        Assert.Equal(OrderStatus.PendingNew, t.Order(r.OrderId!).Status);
        // Erster Trade nach Eingang + Latenz → Fill mit angenommenem Spread (0,10 % → halber Spread 0,05 %)
        t.Trade(Sap, 202m, delayedMinutes: 15, asOf: entry.AddSeconds(1));
        var fill = Assert.Single(t.Fills);
        Assert.Equal(202.101m, fill.Price);
        Assert.Equal(FillQuality.Synthetic, fill.Quality);
        Assert.True(fill.MarketDataAsOfUtc > entry);
    }

    [Fact]
    public void Delayed_stock_quotes_do_not_execute()
    {
        var t = new TestEngine();
        t.Trade(Sap, 200m, delayedMinutes: 15, asOf: t.Now.AddMinutes(-15));
        var r = t.Place(Sap, OrderSide.Buy, OrderType.Limit, 10m, limit: 199m);
        t.Advance(1000);
        t.Engine.OnMarketEvent(new QuoteTick { Symbol = Sap, Source = "gettex", AsOfUtc = t.Now, Bid = 190m, Ask = 190.5m, DelayedMinutes = 15, Quality = DataQuality.Delayed });
        Assert.Equal(OrderStatus.PendingNew, t.Order(r.OrderId!).Status);
        t.Trade(Sap, 200m, qty: 400m, delayedMinutes: 15, asOf: t.Now);  // Annahme, nicht ausführbar
        t.Trade(Sap, 198m, qty: 400m, delayedMinutes: 15, asOf: t.Now);  // durch das Limit
        var f = Assert.Single(t.Fills);
        Assert.Equal(199m, f.Price);
        Assert.Equal(FillQuality.Synthetic, f.Quality);
        Assert.Equal(Liquidity.Maker, f.Liquidity);
    }

    [Fact]
    public void Market_order_rejected_when_market_closed_or_quote_stale()
    {
        var saturday = new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);
        var t = new TestEngine(start: saturday);
        t.Trade(Sap, 200m, delayedMinutes: 15, asOf: t.Now.AddMinutes(-15));
        Assert.Equal("MarketHours", t.Place(Sap, OrderSide.Buy, OrderType.Market, 1m).RiskRule);
        Assert.True(t.Place(Sap, OrderSide.Buy, OrderType.Limit, 1m, limit: 199m).Accepted); // Limit darf warten

        var t2 = new TestEngine();
        t2.Trade(Btc, 100m);
        t2.Advance(TimeSpan.FromMinutes(3).TotalMilliseconds);
        Assert.Equal("MaxQuoteAge", t2.Place(Btc, OrderSide.Buy, OrderType.Market, 1m).RiskRule);

        // Verzögert: Alter relativ zur erwarteten Verzögerung
        var t3 = new TestEngine();
        t3.Trade(Sap, 200m, delayedMinutes: 15, asOf: t3.Now.AddMinutes(-20));
        Assert.True(t3.Place(Sap, OrderSide.Buy, OrderType.Market, 1m).Accepted);
        t3.Trade(Sap, 200m, delayedMinutes: 15, asOf: t3.Now.AddMinutes(-60));
        var t4 = new TestEngine();
        t4.Trade(Sap, 200m, delayedMinutes: 15, asOf: t4.Now.AddMinutes(-60));
        Assert.Equal("MaxQuoteAge", t4.Place(Sap, OrderSide.Buy, OrderType.Market, 1m).RiskRule);

        var t5 = new TestEngine();
        Assert.Equal("NoMarketData", t5.Place(Btc, OrderSide.Buy, OrderType.Market, 1m).RiskRule);
    }

    [Fact]
    public void Stock_day_order_expires_after_session_close_but_late_delayed_data_before_close_still_counts()
    {
        var t = new TestEngine();
        t.Trade(Sap, 200m, delayedMinutes: 15, asOf: t.Now.AddMinutes(-15));
        var r = t.Place(Sap, OrderSide.Buy, OrderType.Limit, 10m, limit: 190m, tif: TimeInForce.Day);
        var close = new DateTimeOffset(2026, 11, 10, 22, 0, 0, TimeSpan.Zero); // 23:00 MEZ
        Assert.Equal(close, t.Order(r.OrderId!).ExpiresUtc);
        t.Clock.Set(close.AddMinutes(14));
        t.Engine.OnClockTick(t.Now);
        Assert.True(t.Order(r.OrderId!).IsOpen);
        t.Trade(Sap, 195m, delayedMinutes: 15, asOf: close.AddMinutes(-1)); // Annahme
        t.Trade(Sap, 189m, qty: 1000m, delayedMinutes: 15, asOf: close.AddMinutes(-1));
        Assert.Equal(OrderStatus.Filled, t.Order(r.OrderId!).Status);

        var r2 = t.Place(Sap, OrderSide.Buy, OrderType.Limit, 10m, limit: 190m, tif: TimeInForce.Day);
        // nach Handelsschluss platziert → gilt bis zum nächsten Handelsschluss
        Assert.Equal(close.AddDays(1), t.Order(r2.OrderId!).ExpiresUtc);
    }
}

public class GapTests
{
    private static Candle C(DateTimeOffset open, decimal o, decimal h, decimal l, decimal c, decimal v)
        => new() { Symbol = Btc, Timeframe = Timeframe.M1, OpenTimeUtc = open, Open = o, High = h, Low = l, Close = c, Volume = v, IsBackfill = true, Source = "kraken" };

    [Fact]
    public void Open_orders_are_checked_against_backfilled_candles_in_order()
    {
        var t = new TestEngine();
        t.Buy(Btc, 1m, 100m);
        var stop = t.Place(Btc, OrderSide.Sell, OrderType.Stop, 1m, stop: 95m);
        var limit = t.Place(Btc, OrderSide.Buy, OrderType.Limit, 1m, limit: 90m);
        var start = t.Now;
        t.Advance(TimeSpan.FromMinutes(10).TotalMilliseconds);
        t.Engine.OnMarketEvent(new GapDetected
        {
            Symbol = Btc,
            Source = "kraken",
            AsOfUtc = t.Now,
            GapStartUtc = start,
            GapEndUtc = t.Now,
            BackfilledCandles =
            [
                C(start.AddMinutes(1), 100m, 101m, 96m, 97m, 10m),
                C(start.AddMinutes(2), 93m, 94m, 92m, 93m, 10m),   // Kurssprung unter den Stop
                C(start.AddMinutes(3), 91m, 92m, 89.5m, 90m, 8m),  // Tief echt unter dem Limit
            ],
        });
        var sf = t.Fills.Single(f => f.OrderId == stop.OrderId);
        Assert.Equal(93m, sf.Price);
        Assert.True(sf.ExecutedDuringGap);
        Assert.Equal(FillQuality.Gap, sf.Quality);
        var lf = t.Fills.Single(f => f.OrderId == limit.OrderId);
        Assert.Equal(90m, lf.Price);
        Assert.Equal(Liquidity.Maker, lf.Liquidity);
        Assert.True(lf.ExecutedDuringGap);
        Assert.Equal(start.AddMinutes(3), lf.MarketDataAsOfUtc);
        Assert.Equal(90m, t.Engine.Market.LastPrice(Btc));
    }

    [Fact]
    public void Stop_inside_candle_fills_at_stop_price()
    {
        var t = new TestEngine();
        t.Buy(Btc, 1m, 100m);
        var stop = t.Place(Btc, OrderSide.Sell, OrderType.Stop, 1m, stop: 95m);
        var start = t.Now;
        t.Advance(120_000);
        t.Engine.OnMarketEvent(new GapDetected
        {
            Symbol = Btc, Source = "kraken", AsOfUtc = t.Now, GapStartUtc = start, GapEndUtc = t.Now,
            BackfilledCandles = [C(start.AddMinutes(1), 99m, 99.5m, 94m, 98m, 5m)],
        });
        Assert.Equal(95m, t.Fills.Single(f => f.OrderId == stop.OrderId).Price);
    }
}
