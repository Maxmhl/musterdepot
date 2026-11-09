using System.Collections.Immutable;
using Musterdepot.Engine.State;

namespace Musterdepot.Engine.Core;

/// <summary>Teil eines FIFO-Lots, der durch einen Verkauf verbraucht wird.</summary>
public sealed record LotSlice(LotState Lot, decimal Quantity, decimal Cost, decimal Fee);

/// <summary>Gemeinsame Buchhaltungsfunktionen für Decide und Evolve (identische Rundung).</summary>
public static class Ledger
{
    /// <summary>Bruttobetrag eines Fills in Basiswährung (Cent-genau).</summary>
    public static decimal Gross(decimal quantity, decimal price, decimal fxRate) => Money.Round(quantity * price * fxRate);

    /// <summary>
    /// Verbraucht <paramref name="quantity"/> nach FIFO. Teilverbrauch eines Lots: Kosten/Gebühr anteilig (Cent-genau gerundet),
    /// Restlot behält die Differenz – Summen bleiben exakt erhalten.
    /// </summary>
    public static (ImmutableList<LotState> Remaining, IReadOnlyList<LotSlice> Slices) Consume(ImmutableList<LotState> lots, decimal quantity)
    {
        var slices = new List<LotSlice>();
        var builder = lots.ToBuilder();
        var left = quantity;
        while (left > 0 && builder.Count > 0)
        {
            var lot = builder[0];
            if (lot.Quantity <= left)
            {
                slices.Add(new LotSlice(lot, lot.Quantity, lot.Cost, lot.Fee));
                left -= lot.Quantity;
                builder.RemoveAt(0);
            }
            else
            {
                var cost = Money.Round(lot.Cost * left / lot.Quantity);
                var fee = Money.Round(lot.Fee * left / lot.Quantity);
                slices.Add(new LotSlice(lot, left, cost, fee));
                builder[0] = lot with { Quantity = lot.Quantity - left, Cost = lot.Cost - cost, Fee = lot.Fee - fee };
                left = 0;
            }
        }
        return (builder.ToImmutable(), slices);
    }

    /// <summary>Realisierter Gewinn eines Verkaufs nach allen Gebühren.</summary>
    public static decimal RealizedNet(decimal gross, decimal sellFee, IReadOnlyList<LotSlice> slices)
        => gross - sellFee - slices.Sum(s => s.Cost + s.Fee);

    /// <summary>Teilt einen Betrag proportional auf Mengen auf; der letzte Teil erhält den Rest (Summe exakt).</summary>
    public static decimal[] Allocate(decimal amount, IReadOnlyList<decimal> quantities)
    {
        var result = new decimal[quantities.Count];
        var total = quantities.Sum();
        decimal used = 0;
        for (int i = 0; i < quantities.Count; i++)
        {
            if (i == quantities.Count - 1) result[i] = amount - used;
            else
            {
                result[i] = total == 0 ? 0 : Money.Round(amount * quantities[i] / total);
                used += result[i];
            }
        }
        return result;
    }
}
