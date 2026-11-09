using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;
using Musterdepot.Core.Events;
using Musterdepot.Engine.Fees;
using static Musterdepot.Engine.Tests.TestEngine;

namespace Musterdepot.Engine.Tests;

public class FeeTests
{
    [Fact]
    public void Fixed_fee_per_order()
        => Assert.Equal(1m, FeeCalculator.Total(new FeeModelConfig { Kind = FeeModelKind.Fixed, FixedFee = 1m }, 5000m, Liquidity.Taker));

    [Fact]
    public void Percent_maker_taker()
    {
        var m = new FeeModelConfig { Kind = FeeModelKind.Percent, MakerRate = 0.0025m, TakerRate = 0.004m, FixedFee = 0 };
        Assert.Equal(2.5m, FeeCalculator.Total(m, 1000m, Liquidity.Maker));
        Assert.Equal(4m, FeeCalculator.Total(m, 1000m, Liquidity.Taker));
        Assert.Equal(0.01m, FeeCalculator.Total(m, 1.30m, Liquidity.Taker)); // 0,0052 → Cent-Rundung
        Assert.Equal(0m, FeeCalculator.Total(m, 1.23m, Liquidity.Taker));    // 0,00492 → 0,00
    }

    [Fact]
    public void Tiered_uses_rate_of_highest_reached_tier()
    {
        var m = new FeeModelConfig { Kind = FeeModelKind.Tiered, Tiers = [new(0m, 0.004m), new(10_000m, 0.002m), new(50_000m, 0.001m)] };
        Assert.Equal(20m, FeeCalculator.Total(m, 5_000m, Liquidity.Taker));
        Assert.Equal(40m, FeeCalculator.Total(m, 20_000m, Liquidity.Taker));
        Assert.Equal(60m, FeeCalculator.Total(m, 60_000m, Liquidity.Taker));
    }

    [Fact]
    public void Combined_with_min_and_max()
    {
        var m = new FeeModelConfig { Kind = FeeModelKind.Combined, FixedFee = 1m, TakerRate = 0.001m, MakerRate = 0.001m, MinFee = 2m, MaxFee = 10m };
        Assert.Equal(2m, FeeCalculator.Total(m, 500m, Liquidity.Taker));     // 1 + 0,5 → Min 2
        Assert.Equal(6m, FeeCalculator.Total(m, 5_000m, Liquidity.Taker));   // 1 + 5
        Assert.Equal(10m, FeeCalculator.Total(m, 50_000m, Liquidity.Taker)); // 1 + 50 → Max 10
        Assert.Equal(0m, FeeCalculator.Total(new FeeModelConfig { Kind = FeeModelKind.None, FixedFee = 5 }, 1000m, Liquidity.Taker));
    }

    [Fact]
    public void Fixed_fee_charged_once_across_partial_fills_and_booked_as_cash_entries()
    {
        var t = new TestEngine(cryptoFees: new FeeModelConfig { Kind = FeeModelKind.Combined, FixedFee = 1m, TakerRate = 0.01m });
        t.Book(Btc, [(99m, 1m)], [(100m, 0.5m), (101m, 1m)]);
        var r = t.Place(Btc, OrderSide.Buy, OrderType.Market, 1m);
        t.Advance(200);
        t.Book(Btc, [], [(100m, 0.5m)], snapshot: false);
        var fills = t.Fills;
        Assert.Equal(2, fills.Count);
        Assert.Equal(1m + 0.5m, fills[0].Fee);   // fest + 1 % von 50
        Assert.Equal(0.51m, fills[1].Fee);       // nur 1 % von 50,50
        Assert.Equal(2.01m, t.Order(r.OrderId!).FeesPaid);
        var entries = t.Engine.GetCashEntries(Acc);
        Assert.Equal(["initial", "buy", "fee", "buy", "fee"], entries.Select(e => e.Kind).ToArray());
        Assert.Equal(100_000m - 50m - 50.5m - 2.01m, t.Portfolio.Cash);
        Assert.Equal(entries.Sum(e => e.Amount), t.Portfolio.Cash);
    }

    [Fact]
    public void Stock_fees_and_crypto_fees_are_separate()
    {
        var t = new TestEngine(cryptoFees: new FeeModelConfig { Kind = FeeModelKind.Percent, TakerRate = 0.004m, FixedFee = 0 }, stockFees: FeeModelConfig.NeoBroker);
        t.Buy(Btc, 1m, 100m);
        Assert.Equal(0.4m, t.Fills[^1].Fee);
        t.Trade(Sap, 200m, delayedMinutes: 15, asOf: t.Now.AddMinutes(-15));
        t.Place(Sap, OrderSide.Buy, OrderType.Market, 1m);
        t.Trade(Sap, 200m, delayedMinutes: 15, asOf: t.Now.AddSeconds(1));
        Assert.Equal(1m, t.Fills[^1].Fee);
        var upd = t.Engine.Execute(new UpdateFeeModelRequest { AccountId = Acc, AssetClass = AssetClass.Stock, Fees = new FeeModelConfig { Kind = FeeModelKind.Fixed, FixedFee = 2m } });
        Assert.True(upd.Accepted);
        Assert.Equal(2m, t.Engine.GetFeeModel(Acc, AssetClass.Stock).FixedFee);
    }
}

public class FxTests
{
    [Fact]
    public void Usd_pair_converted_with_fx_provider_and_rate_in_fill()
    {
        var t = new TestEngine(fx: new FixedFx(0.9m, "ecb"));
        t.Buy("BTC/USD", 2m, 100m);
        var f = t.Fills[^1];
        Assert.Equal(0.9m, f.FxRate);
        Assert.Equal("ecb", t.Events<OrderFilled>().Last().FxSource);
        Assert.Equal(100_000m - 180m, t.Portfolio.Cash);
        Assert.Equal(180m, t.Portfolio.Positions.Single().CostBasis);
        Assert.Equal(180m, t.Portfolio.PositionsValue);
    }

    [Fact]
    public void Falls_back_to_usdc_eur_market_price_or_rejects()
    {
        var t = new TestEngine();
        t.Trade("BTC/USD", 100m);
        Assert.Equal("FxUnavailable", t.Place("BTC/USD", OrderSide.Buy, OrderType.Market, 1m).RiskRule);
        t.Trade("USDC/EUR", 0.92m);
        t.Buy("BTC/USD", 1m, 100m);
        Assert.Equal(0.92m, t.Fills[^1].FxRate);
        Assert.Equal("market:USDC/EUR", t.Events<OrderFilled>().Last().FxSource);
    }
}

public class LedgerTests
{
    [Fact]
    public void Fifo_lots_and_realized_pnl()
    {
        var t = new TestEngine();
        t.Buy(Btc, 1m, 100m);
        t.Buy(Btc, 1m, 200m);
        t.Sell(Btc, 1.5m, 300m);
        var sell = t.Fills[^1];
        Assert.Equal(450m - 100m - 100m, sell.RealizedPnl);
        var pos = t.Portfolio.Positions.Single();
        Assert.Equal(0.5m, pos.Quantity);
        Assert.Equal(200m, pos.AverageCost);
        Assert.Equal(100m, pos.CostBasis);
        Assert.Equal(250m, t.Portfolio.RealizedPnl);
        Assert.Equal(150m - 100m, pos.UnrealizedPnl); // Bewertung zu 300
    }

    [Fact]
    public void Realized_pnl_net_of_fees_and_money_identity()
    {
        var t = new TestEngine(cryptoFees: new FeeModelConfig { Kind = FeeModelKind.Percent, TakerRate = 0.01m, FixedFee = 0 });
        t.Buy(Btc, 1m, 100m);   // Gebühr 1
        t.Buy(Btc, 2m, 150m);   // Gebühr 3
        t.Sell(Btc, 2m, 200m);  // Gebühr 4; FIFO: 1@100(+1) + 1@150(+1,5)
        var sell = t.Fills[^1];
        Assert.Equal(400m - 4m - 100m - 1m - 150m - 1.5m, sell.RealizedPnl);
        var s = t.State;
        Assert.Equal(s.InitialCash + s.TotalDeposits - s.FeesPaid + s.RealizedPnlGross, s.Cash + s.OpenCost);
        Assert.Equal(s.InitialCash + s.TotalDeposits + s.RealizedPnl, s.Cash + s.OpenCostInclFees);
    }

    [Fact]
    public void No_short_selling()
    {
        var t = new TestEngine();
        t.Trade(Btc, 100m);
        var r = t.Place(Btc, OrderSide.Sell, OrderType.Market, 1m);
        Assert.False(r.Accepted);
        Assert.Equal("NoShortSelling", r.RiskRule);
        Assert.Single(t.Events<OrderRejected>());

        t.Buy(Btc, 1m, 100m);
        Assert.True(t.Place(Btc, OrderSide.Sell, OrderType.Limit, 0.6m, limit: 110m).Accepted);
        Assert.Equal("NoShortSelling", t.Place(Btc, OrderSide.Sell, OrderType.Limit, 0.5m, limit: 111m).RiskRule);
        Assert.True(t.Place(Btc, OrderSide.Sell, OrderType.Limit, 0.4m, limit: 111m).Accepted);
    }

    [Fact]
    public void Cash_is_reserved_for_open_buy_orders()
    {
        var t = new TestEngine(initialCash: 1000m);
        t.Trade(Btc, 100m);
        Assert.True(t.Place(Btc, OrderSide.Buy, OrderType.Limit, 9m, limit: 100m).Accepted);
        Assert.Equal(900m, t.Portfolio.ReservedCash);
        var r = t.Place(Btc, OrderSide.Buy, OrderType.Limit, 2m, limit: 100m);
        Assert.Equal("InsufficientCash", r.RiskRule);
        Assert.Equal(100m, t.Portfolio.AvailableCash);
    }

    [Fact]
    public void Deposit_creates_cash_entry()
    {
        var t = new TestEngine();
        Assert.True(t.Engine.Execute(new DepositCashRequest { AccountId = Acc, Amount = 500m }).Accepted);
        Assert.False(t.Engine.Execute(new DepositCashRequest { AccountId = Acc, Amount = -5m }).Accepted);
        Assert.Equal(100_500m, t.Portfolio.Cash);
        Assert.Equal("deposit", t.Engine.GetCashEntries(Acc)[^1].Kind);
    }
}
