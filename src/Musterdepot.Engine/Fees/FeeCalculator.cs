using Musterdepot.Core.Domain;

namespace Musterdepot.Engine.Fees;

/// <summary>
/// Gebührenmodelle (alle Beträge in Basiswährung, Cent-genau):
/// <list type="bullet">
/// <item><b>Fixed</b>: <c>FixedFee</c> je Order.</item>
/// <item><b>Percent</b>: Volumen × MakerRate bzw. TakerRate.</item>
/// <item><b>Tiered</b>: Volumen × Satz der höchsten Stufe mit <c>FromNotional ≤ Ordervolumen</c> (ganze Order zu diesem Satz).</item>
/// <item><b>Combined</b>: <c>FixedFee</c> + prozentualer Anteil (Stufen, falls vorhanden, sonst Maker/Taker).</item>
/// </list>
/// Danach Min/Max je Order. Bei Teilausführungen wird die Gebühr kumulativ auf das bisher ausgeführte Ordervolumen
/// berechnet; jeder Fill trägt die Differenz (feste Gebühr und Mindestgebühr fallen also mit dem ersten Fill an).
/// </summary>
public static class FeeCalculator
{
    /// <summary>Gesamtgebühr einer Order mit dem Volumen <paramref name="orderNotional"/>.</summary>
    public static decimal Total(FeeModelConfig model, decimal orderNotional, Liquidity liquidity)
    {
        if (orderNotional <= 0) return 0m;
        decimal fee = model.Kind switch
        {
            FeeModelKind.None => 0m,
            FeeModelKind.Fixed => model.FixedFee,
            FeeModelKind.Percent => orderNotional * Rate(model, liquidity),
            FeeModelKind.Tiered => orderNotional * TierRate(model, orderNotional, liquidity),
            FeeModelKind.Combined => model.FixedFee + orderNotional * (model.Tiers.Count > 0 ? TierRate(model, orderNotional, liquidity) : Rate(model, liquidity)),
            _ => 0m,
        };
        if (model.Kind != FeeModelKind.None)
        {
            if (fee < model.MinFee) fee = model.MinFee;
            if (model.MaxFee is { } max && fee > max) fee = max;
        }
        return Money.Round(Math.Max(0m, fee));
    }

    /// <summary>Gebühr für einen (Teil-)Fill: kumulative Gesamtgebühr minus bereits gezahlte Gebühren der Order.</summary>
    public static decimal ForFill(FeeModelConfig model, Liquidity liquidity, decimal previousOrderNotional, decimal fillNotional, decimal feesAlreadyPaid)
        => Math.Max(0m, Total(model, previousOrderNotional + fillNotional, liquidity) - feesAlreadyPaid);

    private static decimal Rate(FeeModelConfig m, Liquidity l) => l == Liquidity.Maker ? m.MakerRate : m.TakerRate;

    private static decimal TierRate(FeeModelConfig m, decimal notional, Liquidity l)
    {
        FeeTier? best = null;
        foreach (var t in m.Tiers)
            if (t.FromNotional <= notional && (best is null || t.FromNotional >= best.FromNotional)) best = t;
        return best?.Rate ?? Rate(m, l);
    }
}
