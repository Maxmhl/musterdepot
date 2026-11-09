using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;
using Musterdepot.Core.Events;
using static Musterdepot.Engine.Tests.TestEngine;

namespace Musterdepot.Engine.Tests;

public class RiskTests
{
    private static TestEngine With(Func<RiskConfig, RiskConfig> f, decimal cash = 100_000m) => new(risk: f(Permissive), initialCash: cash);

    private static void AssertRejected(TestEngine t, EngineResult r, string rule)
    {
        Assert.False(r.Accepted);
        Assert.Equal(rule, r.RiskRule);
        var ev = Assert.IsType<OrderRejected>(Assert.Single(r.Events.OfType<OrderRejected>()));
        Assert.Equal(rule, ev.RiskRule);
        Assert.Contains(t.State.Rejections, x => x.OrderId == ev.OrderId);
    }

    [Fact]
    public void Max_order_notional_and_percent_of_equity()
    {
        var t = With(r => r with { MaxOrderNotional = 1000m });
        t.Trade(Btc, 100m);
        AssertRejected(t, t.Place(Btc, OrderSide.Buy, OrderType.Limit, 11m, limit: 100m), "MaxOrderNotional");
        Assert.True(t.Place(Btc, OrderSide.Buy, OrderType.Limit, 10m, limit: 100m).Accepted);

        var t2 = With(r => r with { MaxOrderPercentOfEquity = 10m });
        t2.Trade(Btc, 100m);
        AssertRejected(t2, t2.Place(Btc, OrderSide.Buy, OrderType.Limit, 100.01m, limit: 100m), "MaxOrderPercentOfEquity");
        Assert.True(t2.Place(Btc, OrderSide.Buy, OrderType.Limit, 100m, limit: 100m).Accepted);
    }

    [Fact]
    public void Max_position_notional_and_percent_include_existing_position_and_open_orders()
    {
        var t = With(r => r with { MaxPositionNotional = 5000m });
        t.Buy(Btc, 40m, 100m);
        Assert.True(t.Place(Btc, OrderSide.Buy, OrderType.Limit, 5m, limit: 100m).Accepted);
        AssertRejected(t, t.Place(Btc, OrderSide.Buy, OrderType.Limit, 6m, limit: 100m), "MaxPositionNotional");

        var t2 = With(r => r with { MaxPositionPercentOfEquity = 20m });
        t2.Buy(Btc, 150m, 100m);
        AssertRejected(t2, t2.Place(Btc, OrderSide.Buy, OrderType.Limit, 60m, limit: 100m), "MaxPositionPercentOfEquity");
        Assert.True(t2.Place(Btc, OrderSide.Sell, OrderType.Limit, 150m, limit: 100m).Accepted); // Reduzieren immer möglich
    }

    [Fact]
    public void Daily_loss_limit_blocks_increasing_orders_until_next_day()
    {
        var t = With(r => r with { MaxDailyLoss = 500m });
        t.Buy(Btc, 100m, 100m);
        var openBuy = t.Place(Btc, OrderSide.Buy, OrderType.Limit, 1m, limit: 90m);
        t.Advance(1000);
        t.Trade(Btc, 94m); // −600 → Grenze erreicht
        var hit = Assert.Single(t.Events<DailyLossLimitHit>());
        Assert.Equal(600m, hit.Loss);
        Assert.Equal(500m, hit.Limit);
        Assert.Equal(OrderStatus.Cancelled, t.Order(openBuy.OrderId!).Status);
        Assert.True(t.Portfolio.DailyLossLimitHit);
        AssertRejected(t, t.Place(Btc, OrderSide.Buy, OrderType.Limit, 1m, limit: 94m), "MaxDailyLoss");
        Assert.True(t.Place(Btc, OrderSide.Sell, OrderType.Limit, 10m, limit: 95m).Accepted);

        // Nächster Handelstag (Berlin): neue Tagesstart-Equity
        t.Clock.Set(new DateTimeOffset(2026, 11, 11, 8, 0, 0, TimeSpan.Zero));
        t.Trade(Btc, 94m);
        Assert.True(t.Place(Btc, OrderSide.Buy, OrderType.Limit, 1m, limit: 94m).Accepted);
    }

    [Fact]
    public void Daily_loss_percent_is_checked_at_order_time()
    {
        var t = With(r => r with { MaxDailyLoss = null });
        t.Buy(Btc, 100m, 100m);
        t.Advance(1000);
        t.Trade(Btc, 89.9m); // −1010, aber noch keine Grenze konfiguriert
        Assert.Empty(t.Events<DailyLossLimitHit>());
        Assert.True(t.Engine.Execute(new UpdateRiskConfigRequest { AccountId = Acc, Risk = Permissive with { MaxDailyLoss = null, MaxDailyLossPercent = 1m } }).Accepted);
        var r = t.Place(Btc, OrderSide.Buy, OrderType.Limit, 1m, limit: 89m);
        AssertRejected(t, r, "MaxDailyLoss");
        var hit = Assert.IsType<DailyLossLimitHit>(r.Events[0]);
        Assert.Equal(1000m, hit.Limit);
    }

    [Fact]
    public void Orders_per_minute_stricter_for_ai()
    {
        var t = With(r => r with { MaxOrdersPerMinuteAi = 3, MaxOrdersPerMinuteHuman = 5 });
        t.Trade(Btc, 100m);
        for (int i = 0; i < 3; i++) Assert.True(t.Place(Btc, OrderSide.Buy, OrderType.Limit, 1m, limit: 95m, source: CommandSource.Ai).Accepted);
        AssertRejected(t, t.Place(Btc, OrderSide.Buy, OrderType.Limit, 1m, limit: 95m, source: CommandSource.Ai), "MaxOrdersPerMinute");
        for (int i = 0; i < 5; i++) Assert.True(t.Place(Btc, OrderSide.Buy, OrderType.Limit, 1m, limit: 95m).Accepted);
        AssertRejected(t, t.Place(Btc, OrderSide.Buy, OrderType.Limit, 1m, limit: 95m), "MaxOrdersPerMinute");
        t.Advance(60_001);
        Assert.True(t.Place(Btc, OrderSide.Buy, OrderType.Limit, 1m, limit: 95m, source: CommandSource.Ai).Accepted);
    }

    [Fact]
    public void Price_band_for_limit_stop_and_bracket()
    {
        var t = With(r => r with { PriceBandPercent = 10m });
        t.Trade(Btc, 100m);
        AssertRejected(t, t.Place(Btc, OrderSide.Buy, OrderType.Limit, 1m, limit: 89m), "PriceBand");
        AssertRejected(t, t.Place(Btc, OrderSide.Buy, OrderType.Stop, 1m, stop: 111m), "PriceBand");
        AssertRejected(t, t.Place(Btc, OrderSide.Buy, OrderType.Limit, 1m, limit: 99m, tp: 150m), "PriceBand");
        Assert.True(t.Place(Btc, OrderSide.Buy, OrderType.Limit, 1m, limit: 91m).Accepted);
    }

    [Fact]
    public void Max_open_orders_counts_bracket_children()
    {
        var t = With(r => r with { MaxOpenOrders = 3 });
        t.Trade(Btc, 100m);
        Assert.True(t.Place(Btc, OrderSide.Buy, OrderType.Limit, 1m, limit: 99m).Accepted);
        AssertRejected(t, t.Place(Btc, OrderSide.Buy, OrderType.Limit, 1m, limit: 99m, tp: 105m, sl: 95m), "MaxOpenOrders");
        Assert.True(t.Place(Btc, OrderSide.Buy, OrderType.Limit, 1m, limit: 99m).Accepted);
        Assert.True(t.Place(Btc, OrderSide.Buy, OrderType.Limit, 1m, limit: 99m).Accepted);
        AssertRejected(t, t.Place(Btc, OrderSide.Buy, OrderType.Limit, 1m, limit: 99m), "MaxOpenOrders");
    }

    [Fact]
    public void Allowed_symbols()
    {
        var t = With(r => r with { AllowedSymbols = [Btc] });
        t.Trade("ETH/EUR", 3000m);
        AssertRejected(t, t.Place("ETH/EUR", OrderSide.Buy, OrderType.Limit, 1m, limit: 3000m), "AllowedSymbols");
        t.Trade(Btc, 100m);
        Assert.True(t.Place(Btc, OrderSide.Buy, OrderType.Limit, 1m, limit: 99m).Accepted);
    }

    [Fact]
    public void Kill_switch_engaged_by_ai_cancels_and_blocks_and_only_human_can_release()
    {
        var t = new TestEngine();
        t.Trade(Btc, 100m);
        var o1 = t.Place(Btc, OrderSide.Buy, OrderType.Limit, 1m, limit: 99m);
        var o2 = t.Place(Btc, OrderSide.Buy, OrderType.Limit, 1m, limit: 98m);
        var k = t.Engine.Execute(new EngageKillSwitchRequest { AccountId = Acc, Reason = "KI-Not-Aus", Source = CommandSource.Ai, ClientName = "claude" });
        Assert.True(k.Accepted);
        var ev = Assert.Single(t.Events<KillSwitchEngaged>());
        Assert.Equal(CommandSource.Ai, ev.Source);
        Assert.Equal(OrderStatus.Cancelled, t.Order(o1.OrderId!).Status);
        Assert.Equal(OrderStatus.Cancelled, t.Order(o2.OrderId!).Status);
        Assert.True(t.Engine.IsKillSwitchEngaged(Acc));

        AssertRejected(t, t.Place(Btc, OrderSide.Buy, OrderType.Limit, 1m, limit: 99m), "KillSwitch");
        AssertRejected(t, t.Place(Btc, OrderSide.Buy, OrderType.Limit, 1m, limit: 99m, source: CommandSource.Ai), "KillSwitch");

        var aiRelease = t.Engine.Execute(new ReleaseKillSwitchRequest { AccountId = Acc, ReleasedBy = "claude", Source = CommandSource.Ai });
        Assert.False(aiRelease.Accepted);
        Assert.Equal("KillSwitch", aiRelease.RiskRule);
        Assert.True(t.Engine.IsKillSwitchEngaged(Acc));

        Assert.True(t.Engine.Execute(new ReleaseKillSwitchRequest { AccountId = Acc, ReleasedBy = "Anwender", Source = CommandSource.Human }).Accepted);
        Assert.False(t.Engine.IsKillSwitchEngaged(Acc));
        Assert.True(t.Place(Btc, OrderSide.Buy, OrderType.Limit, 1m, limit: 99m).Accepted);
    }

    [Fact]
    public void Same_client_order_id_returns_existing_order()
    {
        var t = new TestEngine();
        t.Trade(Btc, 100m);
        var r1 = t.Place(Btc, OrderSide.Buy, OrderType.Limit, 1m, limit: 99m, clientOrderId: "ai1:key-1");
        var count = t.Engine.GetEvents(Acc).Count;
        var r2 = t.Place(Btc, OrderSide.Buy, OrderType.Limit, 5m, limit: 90m, clientOrderId: "ai1:key-1");
        Assert.True(r2.Accepted);
        Assert.Equal(r1.OrderId, r2.OrderId);
        Assert.Empty(r2.Events);
        Assert.Equal(count, t.Engine.GetEvents(Acc).Count);
        Assert.Single(t.Engine.GetOrders(Acc));

        // auch nach Neustart (LoadFromEvents)
        var engine2 = new TradingEngine(t.Clock, new Musterdepot.Core.Abstractions.SequentialIdGenerator());
        engine2.LoadFromEvents(t.Engine.GetEvents(Acc));
        Assert.Equal(r1.OrderId, engine2.GetOrderByClientOrderId(Acc, "ai1:key-1")!.OrderId);
        Assert.Equal(r1.OrderId, engine2.Execute(t.Request(Btc, OrderSide.Buy, OrderType.Limit, 1m, limit: 99m, clientOrderId: "ai1:key-1")).OrderId);
    }

    [Fact]
    public void Client_name_is_kept_on_order_and_bracket_children()
    {
        var t = new TestEngine();
        t.Trade(Btc, 100m);
        var req = t.Request(Btc, OrderSide.Buy, OrderType.Limit, 1m, limit: 99m, tp: 105m, sl: 95m, source: CommandSource.Ai) with { ClientName = "claude-code" };
        Assert.True(t.Engine.Execute(req).Accepted);
        Assert.All(t.Engine.GetOrders(Acc), o => Assert.Equal("claude-code", o.ClientName));
    }

    [Fact]
    public void Risk_config_changes_only_by_human()
    {
        var t = new TestEngine();
        var ai = t.Engine.Execute(new UpdateRiskConfigRequest { AccountId = Acc, Risk = RiskConfig.Default with { MaxOrderNotional = null }, Source = CommandSource.Ai });
        Assert.False(ai.Accepted);
        Assert.Equal("HumanOnly", ai.RiskRule);
        Assert.True(t.Engine.Execute(new UpdateRiskConfigRequest { AccountId = Acc, Risk = RiskConfig.Default }).Accepted);
        Assert.Equal(RiskConfig.Default.MaxOrderNotional, t.Engine.GetRiskConfig(Acc).MaxOrderNotional);
    }

    [Fact]
    public void Default_risk_config_rejects_oversized_order()
    {
        var t = new TestEngine(risk: RiskConfig.Default, cryptoFees: FeeModelConfig.KrakenLike);
        t.Trade(Btc, 100m);
        Assert.Equal("MaxOrderNotional", t.Place(Btc, OrderSide.Buy, OrderType.Limit, 101m, limit: 100m).RiskRule);
        var preview = t.Engine.Preview(t.Request(Btc, OrderSide.Buy, OrderType.Limit, 10m, limit: 99m));
        Assert.True(preview.WouldBeAccepted);
        Assert.Equal(990m, preview.EstimatedNotional);
        Assert.Equal(2.48m, preview.EstimatedFee); // Kraken-ähnlich Maker 0,25 %
        Assert.Equal(4m, t.Engine.Preview(t.Request(Btc, OrderSide.Buy, OrderType.Market, 10m)).EstimatedFee); // Taker 0,4 %
        Assert.Equal("MaxOrderNotional", t.Engine.Preview(t.Request(Btc, OrderSide.Buy, OrderType.Limit, 101m, limit: 100m)).RiskRule);
        Assert.Empty(t.Engine.GetOrders(Acc));
    }
}
