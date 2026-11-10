using Musterdepot.Core.Domain;
using Musterdepot.Core.Events;
using static Musterdepot.Engine.Tests.TestEngine;

namespace Musterdepot.Engine.Tests;

public class OrderTypeTests
{
    [Fact]
    public void Market_order_waits_for_latency_and_fills_at_ask()
    {
        var t = new TestEngine();
        t.Quote(Btc, 99m, 101m);
        var r = t.Place(Btc, OrderSide.Buy, OrderType.Market, 1m);
        Assert.True(r.Accepted);
        Assert.Equal(OrderStatus.PendingNew, t.Order(r.OrderId!).Status);
        Assert.Equal(t.Now.AddMilliseconds(200), t.Order(r.OrderId!).EligibleFromUtc);

        t.Advance(100);
        t.Quote(Btc, 99m, 101m); // Daten nach 100 ms: noch nicht prüfen
        Assert.Empty(t.Fills);

        t.Advance(100);
        t.Quote(Btc, 99.5m, 100.5m); // genau Eingang + 200 ms
        var fill = Assert.Single(t.Fills);
        Assert.Equal(100.5m, fill.Price);
        Assert.Equal(Liquidity.Taker, fill.Liquidity);
        Assert.Equal(FillQuality.Trade, fill.Quality);
        Assert.Equal(OrderStatus.Filled, t.Order(r.OrderId!).Status);
        Assert.Contains(t.Events<OrderAccepted>(), e => e.OrderId == r.OrderId);
    }

    [Fact]
    public void Market_order_walks_order_book_level_by_level()
    {
        var t = new TestEngine();
        t.Book(Btc, [(99m, 1m)], [(100m, 0.5m), (101m, 0.5m), (102m, 1m)]);
        var r = t.Place(Btc, OrderSide.Buy, OrderType.Market, 1.2m);
        t.Advance(200);
        // Delta: Stufe 101 wird auf 0,3 reduziert
        t.Book(Btc, [], [(101m, 0.3m)], snapshot: false);

        var fills = t.Fills;
        Assert.Equal(3, fills.Count);
        Assert.Equal([(0.5m, 100m), (0.3m, 101m), (0.4m, 102m)], fills.Select(f => (f.Quantity, f.Price)).ToArray());
        Assert.All(fills, f => Assert.Equal(FillQuality.OrderBook, f.Quality));
        var o = t.Order(r.OrderId!);
        Assert.Equal(OrderStatus.Filled, o.Status);
        Assert.Equal(1.2m, o.FilledQuantity);
        Assert.Equal(Math.Round((50m + 30.3m + 40.8m) / 1.2m, 10), o.AveragePrice);
        Assert.Equal([OrderStatus.PartiallyFilled, OrderStatus.PartiallyFilled, OrderStatus.Filled], t.Events<OrderFilled>().Select(e => e.ResultingStatus).ToArray());
    }

    [Fact]
    public void Market_order_with_too_thin_book_fills_rest_later_and_ioc_cancels_rest()
    {
        var t = new TestEngine();
        t.Book(Btc, [(99m, 1m)], [(100m, 0.5m)]);
        var gtc = t.Place(Btc, OrderSide.Buy, OrderType.Market, 1m);
        t.Advance(200);
        t.Book(Btc, [], [(100m, 0.5m)], snapshot: false);
        Assert.Equal(OrderStatus.PartiallyFilled, t.Order(gtc.OrderId!).Status);
        t.Advance(50);
        t.Book(Btc, [], [(100m, 0m), (100.5m, 2m)], snapshot: false);
        Assert.Equal(OrderStatus.Filled, t.Order(gtc.OrderId!).Status);
        Assert.Equal(100.5m, t.Fills[^1].Price);

        var ioc = t.Place(Btc, OrderSide.Buy, OrderType.Market, 5m, tif: TimeInForce.Ioc);
        t.Advance(200);
        t.Book(Btc, [], [(100.5m, 2m)], snapshot: false);
        var o = t.Order(ioc.OrderId!);
        Assert.Equal(OrderStatus.Cancelled, o.Status);
        Assert.Equal(2m, o.FilledQuantity);
    }

    [Fact]
    public void Fok_fills_completely_or_not_at_all()
    {
        var t = new TestEngine();
        t.Book(Btc, [(99m, 1m)], [(100m, 0.5m), (101m, 0.5m)]);
        var fok = t.Place(Btc, OrderSide.Buy, OrderType.Market, 2m, tif: TimeInForce.Fok);
        t.Advance(200);
        t.Book(Btc, [], [(101m, 0.5m)], snapshot: false);
        Assert.Equal(OrderStatus.Cancelled, t.Order(fok.OrderId!).Status);
        Assert.Empty(t.Fills);

        var fok2 = t.Place(Btc, OrderSide.Buy, OrderType.Limit, 1m, limit: 101m, tif: TimeInForce.Fok);
        t.Advance(200);
        t.Book(Btc, [], [(101m, 0.5m)], snapshot: false);
        Assert.Equal(OrderStatus.Filled, t.Order(fok2.OrderId!).Status);
        Assert.Equal(2, t.Fills.Count);
    }

    [Fact]
    public void Resting_limit_fills_only_when_trade_goes_through_and_is_capped_by_volume_share()
    {
        var t = new TestEngine();
        t.Trade(Btc, 100m);
        var r = t.Place(Btc, OrderSide.Buy, OrderType.Limit, 2m, limit: 99m);
        t.Advance(200);
        t.Trade(Btc, 100m);                  // erste Prüfung: nicht ausführbar → ruht
        Assert.Equal(OrderStatus.Working, t.Order(r.OrderId!).Status);
        t.Trade(Btc, 99m, qty: 100m);        // genau auf dem Limit, Aggressor unbekannt → kein Fill
        Assert.Empty(t.Fills);
        t.Trade(Btc, 98.5m, qty: 4m);        // echt durch: 25 % von 4 = 1
        var fill = Assert.Single(t.Fills);
        Assert.Equal(1m, fill.Quantity);
        Assert.Equal(99m, fill.Price);       // nie schlechter als Limit, Maker zum Limit
        Assert.Equal(Liquidity.Maker, fill.Liquidity);
        t.Trade(Btc, 99m, qty: 8m, aggressor: OrderSide.Sell); // auf dem Limit, von Verkäufer ausgelöst → zählt
        Assert.Equal(OrderStatus.Filled, t.Order(r.OrderId!).Status);
        Assert.All(t.Fills, f => Assert.True(f.Price <= 99m));
    }

    [Fact]
    public void Marketable_limit_takes_liquidity_up_to_limit_then_rests()
    {
        var t = new TestEngine();
        t.Book(Btc, [(99m, 1m)], [(100m, 1m), (100.5m, 1m), (101m, 5m)]);
        var r = t.Place(Btc, OrderSide.Buy, OrderType.Limit, 3m, limit: 100.5m);
        t.Advance(200);
        t.Book(Btc, [], [], snapshot: false);
        var o = t.Order(r.OrderId!);
        Assert.Equal(2m, o.FilledQuantity);
        Assert.Equal(OrderStatus.PartiallyFilled, o.Status);
        Assert.All(t.Fills, f => { Assert.True(f.Price <= 100.5m); Assert.Equal(Liquidity.Taker, f.Liquidity); });
        // Rest ruht: Ask fällt unter das Limit → kein Fill ohne Trade
        t.Book(Btc, [], [(100m, 5m)], snapshot: false);
        Assert.Equal(2m, t.Order(r.OrderId!).FilledQuantity);
    }

    [Fact]
    public void Limit_ioc_without_liquidity_is_cancelled()
    {
        var t = new TestEngine();
        t.Quote(Btc, 99m, 101m);
        var r = t.Place(Btc, OrderSide.Buy, OrderType.Limit, 1m, limit: 100m, tif: TimeInForce.Ioc);
        t.Advance(200);
        t.Quote(Btc, 99m, 101m);
        Assert.Equal(OrderStatus.Cancelled, t.Order(r.OrderId!).Status);
    }

    [Fact]
    public void Stop_triggers_on_last_trade_and_fills_at_first_available_price_on_gap()
    {
        var t = new TestEngine();
        t.Buy(Btc, 1m, 100m);
        var r = t.Place(Btc, OrderSide.Sell, OrderType.Stop, 1m, stop: 95m);
        t.Advance(200);
        t.Trade(Btc, 96m);
        Assert.Empty(t.Events<OrderTriggered>());
        t.Advance(1000);
        t.Trade(Btc, 92m); // Kurssprung über den Stop hinweg
        var trig = Assert.Single(t.Events<OrderTriggered>());
        Assert.Equal(92m, trig.TriggerPrice);
        var fill = t.Fills[^1];
        Assert.Equal(92m, fill.Price); // nicht der Stop-Preis
        Assert.Equal(OrderStatus.Filled, t.Order(r.OrderId!).Status);
    }

    [Fact]
    public void Stop_fills_against_book_after_trigger()
    {
        var t = new TestEngine();
        t.Buy(Btc, 1m, 100m);
        t.Book(Btc, [(94m, 0.4m), (93m, 5m)], [(95m, 1m)]);
        var r = t.Place(Btc, OrderSide.Sell, OrderType.Stop, 1m, stop: 95m);
        t.Advance(300);
        t.Trade(Btc, 94.5m);
        Assert.Equal([(0.4m, 94m), (0.6m, 93m)], t.Fills.Skip(1).Select(f => (f.Quantity, f.Price)).ToArray());
        Assert.Equal(OrderStatus.Filled, t.Order(r.OrderId!).Status);
    }

    [Fact]
    public void Stop_limit_triggers_and_then_behaves_like_limit()
    {
        var t = new TestEngine();
        t.Buy(Btc, 2m, 100m);
        var r = t.Place(Btc, OrderSide.Sell, OrderType.StopLimit, 2m, stop: 95m, limit: 94m);
        t.Advance(200);
        t.Trade(Btc, 93m); // ausgelöst, aber unter dem Limit → ruht
        Assert.Single(t.Events<OrderTriggered>());
        Assert.Equal(OrderStatus.Triggered, t.Order(r.OrderId!).Status);
        Assert.Single(t.Fills);
        t.Trade(Btc, 94.5m, qty: 8m); // echt durch das Limit (Verkauf: Trade > Limit) → 25 % von 8 = 2
        var f = t.Fills[^1];
        Assert.Equal(94m, f.Price);
        Assert.Equal(2m, f.Quantity);
        Assert.Equal(OrderStatus.Filled, t.Order(r.OrderId!).Status);
    }

    [Fact]
    public void Trailing_stop_follows_price_and_triggers()
    {
        var t = new TestEngine();
        t.Buy(Btc, 1m, 100m);
        var r = t.Place(Btc, OrderSide.Sell, OrderType.TrailingStop, 1m, trailPercent: 5m);
        Assert.Equal(95m, t.Order(r.OrderId!).StopPrice);
        t.Advance(200);
        t.Trade(Btc, 110m);
        var adj = Assert.Single(t.Events<TrailingStopAdjusted>());
        Assert.Equal(104.5m, adj.NewStopPrice);
        t.Trade(Btc, 105m); // fällt, Stop bleibt
        Assert.Single(t.Events<TrailingStopAdjusted>());
        t.Trade(Btc, 104m);
        Assert.Single(t.Events<OrderTriggered>());
        Assert.Equal(104m, t.Fills[^1].Price);

        var t2 = new TestEngine();
        t2.Buy(Btc, 1m, 100m);
        var r2 = t2.Place(Btc, OrderSide.Sell, OrderType.TrailingStop, 1m, trailAmount: 3m);
        Assert.Equal(97m, t2.Order(r2.OrderId!).StopPrice);
    }

    [Fact]
    public void Oco_fill_cancels_other_orders_of_group()
    {
        var t = new TestEngine();
        t.Buy(Btc, 1m, 100m);
        var tp = t.Place(Btc, OrderSide.Sell, OrderType.Limit, 1m, limit: 110m, oco: "g1");
        var sl = t.Place(Btc, OrderSide.Sell, OrderType.Stop, 1m, stop: 90m, oco: "g1");
        Assert.True(tp.Accepted && sl.Accepted, sl.RejectReason); // beide teilen sich denselben Bestand
        t.Advance(200);
        t.Trade(Btc, 89m);
        Assert.Equal(OrderStatus.Filled, t.Order(sl.OrderId!).Status);
        Assert.Equal(OrderStatus.Cancelled, t.Order(tp.OrderId!).Status);
    }

    [Fact]
    public void Bracket_children_become_active_after_entry_fill_and_are_oco()
    {
        var t = new TestEngine();
        t.Trade(Btc, 100m);
        var r = t.Place(Btc, OrderSide.Buy, OrderType.Limit, 1m, limit: 99m, tp: 110m, sl: 95m);
        Assert.True(r.Accepted, r.RejectReason);
        var orders = t.Engine.GetOrders(Acc);
        Assert.Equal(3, orders.Count);
        var tp = orders.Single(o => o.BracketRole == BracketRole.TakeProfit);
        var sl = orders.Single(o => o.BracketRole == BracketRole.StopLoss);
        Assert.Equal(r.OrderId, tp.ParentOrderId);
        t.Advance(200);
        t.Trade(Btc, 100m);          // Einstieg angenommen, nicht ausführbar → ruht
        t.Trade(Btc, 94m, qty: 1m); // Einstieg: 25 % von 1 → 0,25 (Teil-Fill), Kinder noch inaktiv → SL nicht ausgelöst
        Assert.Equal(0.25m, t.Order(r.OrderId!).FilledQuantity);
        Assert.Empty(t.Events<OrderTriggered>());
        t.Trade(Btc, 98m, qty: 10m);
        Assert.Equal(OrderStatus.Filled, t.Order(r.OrderId!).Status);
        t.Trade(Btc, 111m, qty: 10m); // TP echt durch → 1 Stück (cap 2,5)
        Assert.Equal(OrderStatus.Filled, t.Order(tp.OrderId).Status);
        Assert.Equal(OrderStatus.Cancelled, t.Order(sl.OrderId).Status);
        Assert.Equal(0m, t.State.PositionQuantity(Btc));
    }

    [Fact]
    public void Bracket_entry_cancelled_unfilled_cancels_children()
    {
        var t = new TestEngine();
        t.Trade(Btc, 100m);
        var r = t.Place(Btc, OrderSide.Buy, OrderType.Limit, 1m, limit: 99m, tp: 110m, sl: 95m);
        t.Engine.Execute(new Musterdepot.Core.Abstractions.CancelOrderRequest { AccountId = Acc, OrderId = r.OrderId! });
        Assert.All(t.Engine.GetOrders(Acc), o => Assert.Equal(OrderStatus.Cancelled, o.Status));
    }

    [Fact]
    public void Day_order_expires_at_midnight_berlin_for_crypto()
    {
        var t = new TestEngine();
        t.Trade(Btc, 100m);
        var r = t.Place(Btc, OrderSide.Buy, OrderType.Limit, 1m, limit: 90m, tif: TimeInForce.Day);
        var expected = new DateTimeOffset(2026, 11, 10, 23, 0, 0, TimeSpan.Zero); // 00:00 MEZ
        Assert.Equal(expected, t.Order(r.OrderId!).ExpiresUtc);
        t.Clock.Set(expected.AddSeconds(-1));
        t.Engine.OnClockTick(t.Now);
        Assert.True(t.Order(r.OrderId!).IsOpen);
        t.Clock.Set(expected);
        t.Engine.OnClockTick(t.Now);
        Assert.Equal(OrderStatus.Expired, t.Order(r.OrderId!).Status);
    }

    [Fact]
    public void Gtd_order_expires_at_expiry()
    {
        var t = new TestEngine();
        t.Trade(Btc, 100m);
        var r = t.Place(Btc, OrderSide.Buy, OrderType.Limit, 1m, limit: 90m, tif: TimeInForce.Gtd, expires: t.Now.AddHours(1));
        t.AdvanceAndTick(TimeSpan.FromMinutes(59));
        Assert.True(t.Order(r.OrderId!).IsOpen);
        t.AdvanceAndTick(TimeSpan.FromMinutes(1));
        Assert.Equal(OrderStatus.Expired, t.Order(r.OrderId!).Status);

        var bad = t.Place(Btc, OrderSide.Buy, OrderType.Limit, 1m, limit: 90m, tif: TimeInForce.Gtd);
        Assert.False(bad.Accepted);
    }

    [Fact]
    public void Notional_order_computes_quantity_from_amount()
    {
        var t = new TestEngine();
        t.Quote(Btc, 99m, 100m);
        var r = t.Place(Btc, OrderSide.Buy, OrderType.Market, notional: 250m);
        Assert.Equal(2.5m, t.Order(r.OrderId!).Quantity);
    }

    [Fact]
    public void Modify_and_cancel_order()
    {
        var t = new TestEngine();
        t.Trade(Btc, 100m);
        var r = t.Place(Btc, OrderSide.Buy, OrderType.Limit, 1m, limit: 95m);
        var m = t.Engine.Execute(new Musterdepot.Core.Abstractions.ModifyOrderRequest { AccountId = Acc, OrderId = r.OrderId!, NewLimitPrice = 96m, NewQuantity = 2m });
        Assert.True(m.Accepted, m.RejectReason);
        Assert.Equal(96m, t.Order(r.OrderId!).LimitPrice);
        Assert.Equal(2m, t.Order(r.OrderId!).Quantity);
        var bad = t.Engine.Execute(new Musterdepot.Core.Abstractions.ModifyOrderRequest { AccountId = Acc, OrderId = r.OrderId!, NewLimitPrice = 10m });
        Assert.Equal("PriceBand", bad.RiskRule);
        var c = t.Engine.Execute(new Musterdepot.Core.Abstractions.CancelOrderRequest { AccountId = Acc, OrderId = r.OrderId! });
        Assert.True(c.Accepted);
        Assert.Equal(OrderStatus.Cancelled, t.Order(r.OrderId!).Status);
        Assert.False(t.Engine.Execute(new Musterdepot.Core.Abstractions.CancelOrderRequest { AccountId = Acc, OrderId = r.OrderId! }).Accepted);
    }
}
