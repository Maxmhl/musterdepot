using Musterdepot.Core.Domain;
using Musterdepot.Engine.Calendar;
using Musterdepot.Engine.Fees;
using Musterdepot.Engine.Market;
using Musterdepot.Engine.State;

namespace Musterdepot.Engine.Core;

public static partial class TradingCore
{
    /// <summary>Bewertungskurs (Quote-Währung) und Umrechnungskurs eines Symbols: Live-Markt, sonst gespeicherter Mark.</summary>
    public static (decimal Price, decimal Fx, bool Live)? ValuationPrice(AccountState s, string symbol, MarketState market, EngineContext ctx)
    {
        var mark = s.Marks.TryGetValue(symbol, out var m) ? m : null;
        var live = market.LastPrice(symbol);
        var inst = ctx.Instruments.Resolve(symbol);
        decimal fx = mark?.FxRate ?? (s.Lots.TryGetValue(symbol, out var lots) && lots.Count > 0 ? lots[^1].FxRate : 1m);
        if (inst is not null && ctx.ResolveFx(inst.QuoteCurrency, s.BaseCurrency, market) is { } r) fx = r.Rate;
        if (live is { } p) return (p, fx, true);
        if (mark is not null) return (mark.Price, fx, false);
        return null;
    }

    /// <summary>Positionswert zu aktuellen Kursen (Live-Markt, sonst Mark, sonst Einstand).</summary>
    public static decimal LivePositionsValue(AccountState s, MarketState market, EngineContext ctx)
    {
        decimal total = 0;
        foreach (var (symbol, lots) in s.Lots)
        {
            if (lots.Count == 0) continue;
            var qty = lots.Sum(l => l.Quantity);
            total += ValuationPrice(s, symbol, market, ctx) is { } v ? Money.Round(qty * v.Price * v.Fx) : lots.Sum(l => l.Cost);
        }
        return total;
    }

    public static decimal LiveEquity(AccountState s, MarketState market, EngineContext ctx) => s.Cash + LivePositionsValue(s, market, ctx);

    /// <summary>Geschätzter Ausführungspreis für Reservierung/Notional (Quote-Währung).</summary>
    internal static decimal? EstimatePrice(Order o, MarketState market, EngineContext ctx)
    {
        var sym = market.Get(o.Symbol);
        var last = sym?.LastPrice;
        return o.Type switch
        {
            OrderType.Limit or OrderType.StopLimit => o.LimitPrice,
            OrderType.Stop => o.StopPrice is { } sp && last is { } l ? (o.Side == OrderSide.Buy ? Math.Max(sp, l) : Math.Min(sp, l)) : o.StopPrice ?? last,
            OrderType.TrailingStop => o.StopPrice ?? last,
            _ => MarketReference(sym, o.Side, ctx),
        };
    }

    /// <summary>Referenzpreis für Market-Orders: Ask/Bid, sonst letzter Kurs (verzögert: ± halber angenommener Spread).</summary>
    internal static decimal? MarketReference(SymbolMarketState? sym, OrderSide side, EngineContext ctx)
    {
        if (sym is null) return null;
        if (sym.IsDelayed)
        {
            if (sym.LastPrice is not { } p) return null;
            var half = ctx.Execution.SyntheticSpreadPercent / 200m;
            return side == OrderSide.Buy ? p * (1 + half) : p * (1 - half);
        }
        return (side == OrderSide.Buy ? sym.BestAsk : sym.BestBid) ?? sym.LastPrice;
    }

    /// <summary>Für offene Kauforders reserviertes Geld (geschätzter Restbetrag inkl. Gebühr). Wird bei Bedarf berechnet.</summary>
    public static decimal ReservedCash(AccountState s, MarketState market, EngineContext ctx, string? excludeOrderId = null)
    {
        decimal total = 0;
        foreach (var o in s.OpenOrders)
        {
            if (o.Side != OrderSide.Buy || o.OrderId == excludeOrderId) continue;
            total += EstimatedOrderCost(s, o, o.RemainingQuantity, market, ctx);
        }
        return total;
    }

    internal static decimal EstimatedOrderCost(AccountState s, Order o, decimal quantity, MarketState market, EngineContext ctx)
    {
        if (quantity <= 0 || EstimatePrice(o, market, ctx) is not { } price) return 0m;
        var inst = ctx.Instruments.Resolve(o.Symbol);
        var fx = inst is not null && ctx.ResolveFx(inst.QuoteCurrency, s.BaseCurrency, market) is { } r ? r.Rate : 1m;
        var notional = Ledger.Gross(quantity, price, fx);
        var model = s.FeeModel(inst?.AssetClass ?? AssetClass.Crypto);
        var prev = s.OrderFilledNotional.TryGetValue(o.OrderId, out var n) ? n : 0m;
        var liq = o.Type is OrderType.Limit ? Liquidity.Maker : Liquidity.Taker;
        var fee = FeeCalculator.ForFill(model, liq, prev, notional, o.FeesPaid);
        // Taker-Gebühr als Obergrenze (Maker kann bei sofort ausführbaren Limits zu Taker werden).
        fee = Math.Max(fee, FeeCalculator.ForFill(model, Liquidity.Taker, prev, notional, o.FeesPaid));
        return notional + fee;
    }

    /// <summary>Bracket-Kind aktiv? Erst nach (Teil-)Ausführung und Abschluss des Einstiegs.</summary>
    public static bool IsActive(AccountState s, Order o)
    {
        if (o.ParentOrderId is null) return true;
        if (!s.Orders.TryGetValue(o.ParentOrderId, out var parent)) return true;
        return parent.Status == OrderStatus.Filled || (!parent.IsOpen && parent.FilledQuantity > 0);
    }

    /// <summary>Durch offene Verkaufsorders gebundene Stückzahl (OCO-Gruppen zählen mit ihrem Maximum).</summary>
    internal static decimal ReservedSellQuantity(AccountState s, string symbol, Order? additional = null, string? excludeOrderId = null)
    {
        var groups = new Dictionary<string, decimal>(StringComparer.Ordinal);
        IEnumerable<Order> orders = s.OpenOrders.Where(o => o.Symbol == symbol && o.Side == OrderSide.Sell && o.OrderId != excludeOrderId && IsActive(s, o));
        if (additional is not null) orders = orders.Append(additional);
        foreach (var o in orders)
        {
            var key = o.OcoGroupId ?? o.OrderId;
            groups[key] = Math.Max(groups.TryGetValue(key, out var v) ? v : 0m, o.RemainingQuantity);
        }
        return groups.Values.Sum();
    }

    /// <summary>Effektive Tagesverlustgrenze (Minimum aus absolut und Prozent der Tagesstart-Equity).</summary>
    internal static decimal? DailyLossLimit(RiskConfig r, decimal dayStartEquity)
    {
        decimal? limit = r.MaxDailyLoss;
        if (r.MaxDailyLossPercent is { } pct)
        {
            var p = Money.Round(dayStartEquity * pct / 100m);
            limit = limit is { } l ? Math.Min(l, p) : p;
        }
        return limit;
    }

    internal static (decimal Loss, decimal Limit)? CurrentDailyLoss(AccountState s, MarketState market, EngineContext ctx)
    {
        var day = BerlinTime.TradingDay(ctx.Now);
        var dayStart = s.DayStartEquityFor(day);
        if (DailyLossLimit(s.Risk, dayStart) is not { } limit) return null;
        var loss = dayStart - LiveEquity(s, market, ctx);
        return (loss, limit);
    }

    /// <summary>Zulässiges Alter von Kursdaten (verzögerte Quellen: relativ zur erwarteten Verzögerung).</summary>
    internal static TimeSpan AllowedQuoteAge(SymbolMarketState? sym, RiskConfig risk, EngineContext ctx)
        => sym is { IsDelayed: true }
            ? risk.MaxQuoteAge + TimeSpan.FromMinutes(sym.DelayedMinutes) + ctx.Options.DelayedDataTolerance
            : risk.MaxQuoteAge;
}
