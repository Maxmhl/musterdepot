using Musterdepot.Core.Domain;
using Musterdepot.Engine.State;

namespace Musterdepot.Engine.Stats;

/// <summary>
/// Kennzahlen aus FIFO-Round-Trips (Paar Eröffnungs-/Schließungsorder). Zuordnung zu Quelle/Strategie über die eröffnende Order.
/// Prozentwerte (Gewinnquote, Gebührenanteil, Drawdown) in Prozent (0–100).
/// </summary>
public static class PerformanceCalculator
{
    /// <summary>Profit Factor ohne Verluste wird auf diesen Wert begrenzt.</summary>
    public const decimal MaxProfitFactor = 999m;

    public static PerformanceStats Compute(AccountState s, CommandSource? source = null, string? strategyTag = null)
    {
        bool Match(CommandSource src, string? tag)
            => (source is null || src == source) && (strategyTag is null || string.Equals(tag, strategyTag, StringComparison.Ordinal));

        var trips = s.RoundTrips.Values.Where(t => Match(t.Source, t.StrategyTag))
            .OrderBy(t => t.ClosedUtc).ThenBy(t => t.Key, StringComparer.Ordinal).ToList();
        var wins = trips.Where(t => t.NetPnl > 0).ToList();
        var losses = trips.Where(t => t.NetPnl < 0).ToList();
        var grossProfit = wins.Sum(t => t.NetPnl);
        var grossLoss = losses.Sum(t => t.NetPnl);
        var net = trips.Sum(t => t.NetPnl);
        var tripFees = trips.Sum(t => t.Fees);
        var tripGross = trips.Sum(t => t.GrossPnl);
        var totalFees = s.Fills.Where(f => Match(f.Source, f.StrategyTag)).Sum(f => f.Fee);

        var (dd, ddPct) = source is null && strategyTag is null ? EquityDrawdown(s) : TradeDrawdown(s, trips);

        return new PerformanceStats
        {
            TradeCount = trips.Count,
            Wins = wins.Count,
            Losses = losses.Count,
            WinRate = trips.Count == 0 ? 0 : Pct(wins.Count, trips.Count),
            ProfitFactor = grossLoss == 0 ? (grossProfit > 0 ? MaxProfitFactor : 0) : Math.Min(MaxProfitFactor, Math.Round(grossProfit / -grossLoss, 4)),
            Expectancy = trips.Count == 0 ? 0 : Money.Round(net / trips.Count),
            GrossProfit = grossProfit,
            GrossLoss = grossLoss,
            NetPnl = net,
            TotalFees = totalFees,
            FeeSharePercent = tripGross == 0 ? 0 : Pct(tripFees, Math.Abs(tripGross)),
            MaxDrawdown = dd,
            MaxDrawdownPercent = ddPct,
            AverageHoldingTime = trips.Count == 0 ? TimeSpan.Zero : TimeSpan.FromTicks((long)trips.Average(t => (t.ClosedUtc - t.OpenedUtc).Ticks)),
            AverageWin = wins.Count == 0 ? 0 : Money.Round(grossProfit / wins.Count),
            AverageLoss = losses.Count == 0 ? 0 : Money.Round(grossLoss / losses.Count),
            Source = source,
            StrategyTag = strategyTag,
        };
    }

    private static decimal Pct(decimal part, decimal whole) => whole == 0 ? 0 : Math.Round(part / whole * 100m, 2, MidpointRounding.AwayFromZero);

    /// <summary>Max. Drawdown der Equity-Kurve, bereinigt um spätere Ein-/Auszahlungen.</summary>
    public static (decimal Abs, decimal Pct) EquityDrawdown(AccountState s)
    {
        var flows = s.CashEntries.Where(c => c.Kind is "deposit" or "withdrawal").OrderBy(c => c.TimestampUtc).ToList();
        decimal peak = decimal.MinValue, maxDd = 0, maxPct = 0;
        int fi = 0;
        decimal cumFlows = 0;
        foreach (var p in s.EquityCurve)
        {
            while (fi < flows.Count && flows[fi].TimestampUtc <= p.TimestampUtc) cumFlows += flows[fi++].Amount;
            var v = p.Equity - cumFlows;
            if (v > peak) peak = v;
            var dd = peak - v;
            if (dd > maxDd) { maxDd = dd; maxPct = peak > 0 ? Pct(dd, peak) : 0; }
        }
        return (maxDd, maxPct);
    }

    /// <summary>Max. Drawdown der kumulierten Round-Trip-Ergebnisse (Basis: Anfangskapital).</summary>
    public static (decimal Abs, decimal Pct) TradeDrawdown(AccountState s, IReadOnlyList<RoundTrip> trips)
    {
        decimal v = s.InitialCash, peak = v, maxDd = 0, maxPct = 0;
        foreach (var t in trips)
        {
            v += t.NetPnl;
            if (v > peak) peak = v;
            var dd = peak - v;
            if (dd > maxDd) { maxDd = dd; maxPct = peak > 0 ? Pct(dd, peak) : 0; }
        }
        return (maxDd, maxPct);
    }
}
