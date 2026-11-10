using System.Collections.Immutable;
using Musterdepot.Core.Domain;
using Musterdepot.Core.Events;

namespace Musterdepot.Engine.State;

/// <summary>FIFO-Einstandsposten (intern, mit getrennten Kosten- und Gebührenanteilen für exakte Buchhaltung).</summary>
public sealed record LotState
{
    public required string LotId { get; init; }
    public required string Symbol { get; init; }
    public required string OrderId { get; init; }
    public required string OpeningFillId { get; init; }
    /// <summary>Verbleibende Menge.</summary>
    public required decimal Quantity { get; init; }
    public required decimal OriginalQuantity { get; init; }
    /// <summary>Kaufpreis je Stück in Quote-Währung.</summary>
    public required decimal Price { get; init; }
    public required decimal FxRate { get; init; }
    /// <summary>Verbleibender Einstand ohne Gebühren (Basiswährung).</summary>
    public required decimal Cost { get; init; }
    /// <summary>Verbleibender Anteil der Kaufgebühr (Basiswährung).</summary>
    public required decimal Fee { get; init; }
    public required DateTimeOffset OpenedUtc { get; init; }
    public CommandSource Source { get; init; }
    public string? StrategyTag { get; init; }

    public decimal CostInclFees => Cost + Fee;

    public Lot ToLot() => new()
    {
        LotId = LotId,
        Symbol = Symbol,
        Quantity = Quantity,
        Price = Price,
        CostPerUnitBase = Quantity == 0 ? 0 : Math.Round(CostInclFees / Quantity, 10, MidpointRounding.AwayFromZero),
        OpenedUtc = OpenedUtc,
        OpeningFillId = OpeningFillId,
        Source = Source,
        StrategyTag = StrategyTag,
    };
}

/// <summary>Bewertungskurs eines Symbols (Quote-Währung) samt Umrechnungskurs in die Basiswährung.</summary>
public sealed record MarkState(decimal Price, decimal FxRate, DateTimeOffset AsOfUtc);

/// <summary>Abgeschlossener Round-Trip (FIFO): Paar aus Eröffnungs- und Schließungsorder.</summary>
public sealed record RoundTrip
{
    public required string Key { get; init; }
    public required string Symbol { get; init; }
    public required string OpenOrderId { get; init; }
    public required string CloseOrderId { get; init; }
    public decimal Quantity { get; init; }
    /// <summary>Einstand ohne Gebühren.</summary>
    public decimal EntryCost { get; init; }
    /// <summary>Verkaufserlös ohne Gebühren.</summary>
    public decimal ExitProceeds { get; init; }
    /// <summary>Kauf- und Verkaufsgebühren dieses Round-Trips.</summary>
    public decimal Fees { get; init; }
    public decimal GrossPnl => ExitProceeds - EntryCost;
    public decimal NetPnl => GrossPnl - Fees;
    public required DateTimeOffset OpenedUtc { get; init; }
    public required DateTimeOffset ClosedUtc { get; init; }
    /// <summary>Quelle/Strategie der eröffnenden Order.</summary>
    public CommandSource Source { get; init; }
    public string? StrategyTag { get; init; }
}

public sealed record SubmissionStamp(DateTimeOffset TimestampUtc, CommandSource Source);

/// <summary>
/// Zustand eines Kontos. Unveränderlich; entsteht ausschließlich durch <see cref="Core.TradingCore.Evolve"/> aus Domain-Events.
/// Sortierte Sammlungen (ordinal) sorgen für deterministische Serialisierung (LoadFromEvents ≡ Live-Zustand).
/// </summary>
public sealed record AccountState
{
    public required Account Account { get; init; }
    public string AccountId => Account.AccountId;
    public string BaseCurrency => Account.BaseCurrency;

    public decimal Cash { get; init; }
    public decimal InitialCash { get; init; }
    public decimal TotalDeposits { get; init; }
    public decimal TotalWithdrawals { get; init; }
    public decimal FeesPaid { get; init; }
    /// <summary>Realisierter Gewinn nach allen Gebühren (Verkaufsgebühr + anteilige Kaufgebühr).</summary>
    public decimal RealizedPnl { get; init; }
    /// <summary>Realisierter Gewinn vor Gebühren.</summary>
    public decimal RealizedPnlGross { get; init; }

    public RiskConfig Risk { get; init; } = RiskConfig.Default;
    public FeeModelConfig CryptoFees { get; init; } = FeeModelConfig.KrakenLike;
    public FeeModelConfig StockFees { get; init; } = FeeModelConfig.NeoBroker;

    public ImmutableSortedDictionary<string, Order> Orders { get; init; } = ImmutableSortedDictionary.Create<string, Order>(StringComparer.Ordinal);
    /// <summary>Order-IDs in Eingangsreihenfolge.</summary>
    public ImmutableList<string> OrderSequence { get; init; } = [];
    /// <summary>IDs der offenen Orders in Eingangsreihenfolge (schneller Zugriff bei jedem Marktereignis).</summary>
    public ImmutableList<string> OpenOrderIds { get; init; } = [];
    public ImmutableSortedDictionary<string, string> ClientOrderIndex { get; init; } = ImmutableSortedDictionary.Create<string, string>(StringComparer.Ordinal);
    /// <summary>Bisher ausgeführtes Volumen je Order in Basiswährung (für kumulative Gebühren).</summary>
    public ImmutableSortedDictionary<string, decimal> OrderFilledNotional { get; init; } = ImmutableSortedDictionary.Create<string, decimal>(StringComparer.Ordinal);

    public ImmutableSortedDictionary<string, ImmutableList<LotState>> Lots { get; init; } = ImmutableSortedDictionary.Create<string, ImmutableList<LotState>>(StringComparer.Ordinal);
    public ImmutableSortedDictionary<string, decimal> RealizedBySymbol { get; init; } = ImmutableSortedDictionary.Create<string, decimal>(StringComparer.Ordinal);
    public ImmutableSortedDictionary<string, MarkState> Marks { get; init; } = ImmutableSortedDictionary.Create<string, MarkState>(StringComparer.Ordinal);

    public ImmutableList<Fill> Fills { get; init; } = [];
    public ImmutableList<CashEntry> CashEntries { get; init; } = [];
    public ImmutableList<EquitySnapshot> EquityCurve { get; init; } = [];
    public ImmutableSortedDictionary<string, RoundTrip> RoundTrips { get; init; } = ImmutableSortedDictionary.Create<string, RoundTrip>(StringComparer.Ordinal);
    public ImmutableList<OrderRejected> Rejections { get; init; } = [];

    public bool KillSwitchEngaged { get; init; }
    public string? KillSwitchReason { get; init; }

    public DateOnly TradingDay { get; init; }
    public decimal DayStartEquity { get; init; }
    public DateOnly? DailyLossHitDay { get; init; }

    public ImmutableList<SubmissionStamp> RecentSubmissions { get; init; } = [];
    public DateTimeOffset? LastMarkUtc { get; init; }
    public long LastSequence { get; init; }

    // ---------- abgeleitete Werte ----------

    public FeeModelConfig FeeModel(AssetClass assetClass) => assetClass == AssetClass.Crypto ? CryptoFees : StockFees;

    public decimal PositionQuantity(string symbol)
        => Lots.TryGetValue(symbol, out var lots) ? lots.Sum(l => l.Quantity) : 0m;

    public IEnumerable<string> HeldSymbols => Lots.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key);

    public IEnumerable<Order> OpenOrders => OpenOrderIds.Select(id => Orders[id]);

    /// <summary>Einstand offener Positionen ohne Gebühren.</summary>
    public decimal OpenCost => Lots.Values.SelectMany(l => l).Sum(l => l.Cost);
    /// <summary>Einstand offener Positionen inkl. anteiliger Kaufgebühren.</summary>
    public decimal OpenCostInclFees => Lots.Values.SelectMany(l => l).Sum(l => l.CostInclFees);

    /// <summary>Positionswert zu den gespeicherten Bewertungskursen (Marks), sonst zum Einstand.</summary>
    public decimal MarkedPositionsValue()
    {
        decimal total = 0m;
        foreach (var (symbol, lots) in Lots)
        {
            if (lots.Count == 0) continue;
            var qty = lots.Sum(l => l.Quantity);
            total += Marks.TryGetValue(symbol, out var m) ? Money.Round(qty * m.Price * m.FxRate) : lots.Sum(l => l.Cost);
        }
        return total;
    }

    public decimal MarkedEquity() => Cash + MarkedPositionsValue();

    /// <summary>Tagesstart-Equity für den Handelstag <paramref name="day"/> (vor dem ersten Event des Tages = aktuelle Equity).</summary>
    public decimal DayStartEquityFor(DateOnly day) => day == TradingDay ? DayStartEquity : MarkedEquity();

    public bool IsDailyLossLimitHit(DateOnly day) => DailyLossHitDay == day;

    public Order? FindByClientOrderId(string clientOrderId)
        => ClientOrderIndex.TryGetValue(clientOrderId, out var id) && Orders.TryGetValue(id, out var o) ? o : null;
}
