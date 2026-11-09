namespace Musterdepot.Core.Domain;

public sealed record PositionSnapshot
{
    public required string Symbol { get; init; }
    public required decimal Quantity { get; init; }
    /// <summary>Durchschnittlicher Einstand je Stück (Quote-Währung, FIFO-Lots gewichtet).</summary>
    public required decimal AverageCost { get; init; }
    /// <summary>Einstand gesamt in Basiswährung inkl. Kaufgebühren.</summary>
    public required decimal CostBasis { get; init; }
    public decimal? LastPrice { get; init; }
    public decimal? MarketValue { get; init; }
    public decimal? UnrealizedPnl { get; init; }
    public decimal? UnrealizedPnlPercent { get; init; }
    public decimal RealizedPnl { get; init; }
    public string QuoteCurrency { get; init; } = "EUR";
    public bool PriceIsDelayed { get; init; }
    public DateTimeOffset? PriceAsOfUtc { get; init; }
    public IReadOnlyList<Lot> Lots { get; init; } = [];
}

public sealed record PortfolioSnapshot
{
    public required string AccountId { get; init; }
    public string BaseCurrency { get; init; } = "EUR";
    public required DateTimeOffset AsOfUtc { get; init; }
    public required decimal Cash { get; init; }
    /// <summary>Für offene Kauforders reserviertes Geld.</summary>
    public decimal ReservedCash { get; init; }
    public decimal AvailableCash => Cash - ReservedCash;
    public required decimal PositionsValue { get; init; }
    public decimal Equity => Cash + PositionsValue;
    public decimal RealizedPnl { get; init; }
    public decimal UnrealizedPnl { get; init; }
    public decimal FeesPaid { get; init; }
    public decimal DayPnl { get; init; }
    public decimal DayStartEquity { get; init; }
    public bool KillSwitchEngaged { get; init; }
    public bool DailyLossLimitHit { get; init; }
    public required IReadOnlyList<PositionSnapshot> Positions { get; init; }
}

public sealed record PerformanceStats
{
    public int TradeCount { get; init; }
    public int Wins { get; init; }
    public int Losses { get; init; }
    public decimal WinRate { get; init; }
    public decimal ProfitFactor { get; init; }
    /// <summary>Erwartungswert je Trade (EUR).</summary>
    public decimal Expectancy { get; init; }
    public decimal GrossProfit { get; init; }
    public decimal GrossLoss { get; init; }
    public decimal NetPnl { get; init; }
    public decimal TotalFees { get; init; }
    /// <summary>Gebührenanteil am Bruttoergebnis in %.</summary>
    public decimal FeeSharePercent { get; init; }
    public decimal MaxDrawdown { get; init; }
    public decimal MaxDrawdownPercent { get; init; }
    public TimeSpan AverageHoldingTime { get; init; }
    public decimal AverageWin { get; init; }
    public decimal AverageLoss { get; init; }
    public CommandSource? Source { get; init; }
    public string? StrategyTag { get; init; }
}

/// <summary>Vorschau einer Order (dry_run): Gebühren, Risikoprüfung, geschätzter Preis.</summary>
public sealed record OrderPreview
{
    public required bool WouldBeAccepted { get; init; }
    public string? RejectReason { get; init; }
    public string? RiskRule { get; init; }
    public decimal Quantity { get; init; }
    public decimal? EstimatedPrice { get; init; }
    public decimal? EstimatedNotional { get; init; }
    public decimal EstimatedFee { get; init; }
    public decimal? EstimatedCashAfter { get; init; }
    public bool PriceIsDelayed { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
