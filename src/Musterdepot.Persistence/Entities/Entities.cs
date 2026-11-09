namespace Musterdepot.Persistence.Entities;

// Tabellenzeilen (EF Core). Geldbeträge sind decimal und werden als TEXT (invariant, verlustfrei) gespeichert,
// Zeitpunkte als TEXT im UTC-ISO-8601-Format (lexikografisch sortierbar), Enums als TEXT.

/// <summary>Schema-Version der Datenbank (genau eine Zeile, Id = 1).</summary>
public sealed class SchemaInfoEntity
{
    public int Id { get; set; } = 1;
    public int Version { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public string? AppVersion { get; set; }
}

/// <summary>Projektion: Konto (aus AccountOpened, Kontobuchungen, Fills, Not-Aus, Risiko/Gebühren).</summary>
public sealed class AccountEntity
{
    public string AccountId { get; set; } = "";
    public string Name { get; set; } = "";
    public string BaseCurrency { get; set; } = "EUR";
    public DateTimeOffset CreatedUtc { get; set; }
    public decimal InitialCash { get; set; }
    /// <summary>Kontostand Cash (Basiswährung) laut Projektion.</summary>
    public decimal Cash { get; set; }
    public decimal FeesPaid { get; set; }
    public decimal RealizedPnl { get; set; }
    public bool KillSwitchEngaged { get; set; }
    public DateTimeOffset? LastDailyLossLimitHitUtc { get; set; }
    public string? RiskJson { get; set; }
    public string? CryptoFeesJson { get; set; }
    public string? StockFeesJson { get; set; }
    public long LastSequence { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
}

public sealed class InstrumentEntity
{
    public string Symbol { get; set; } = "";
    public string Name { get; set; } = "";
    public string AssetClass { get; set; } = "";
    public string QuoteCurrency { get; set; } = "";
    public string? BaseAsset { get; set; }
    public string? Isin { get; set; }
    public string? Wkn { get; set; }
    public decimal TickSize { get; set; }
    public decimal QuantityStep { get; set; }
    public decimal MinQuantity { get; set; }
    public string? PrimarySource { get; set; }
    public string? SourceSymbol { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
}

public sealed class WatchlistEntity
{
    public string WatchlistId { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; }
    public List<WatchlistItemEntity> Items { get; set; } = [];
}

public sealed class WatchlistItemEntity
{
    public long Id { get; set; }
    public string WatchlistId { get; set; } = "";
    public string Symbol { get; set; } = "";
    public int Position { get; set; }
}

/// <summary>Projektion: aktueller Order-Zustand.</summary>
public sealed class OrderEntity
{
    public string OrderId { get; set; } = "";
    public string ClientOrderId { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Side { get; set; } = "";
    public string Type { get; set; } = "";
    public string TimeInForce { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal? LimitPrice { get; set; }
    public decimal? StopPrice { get; set; }
    public decimal? TrailAmount { get; set; }
    public decimal? TrailPercent { get; set; }
    public decimal? TrailReferencePrice { get; set; }
    public DateTimeOffset? ExpiresUtc { get; set; }
    public string Source { get; set; } = "";
    public string? ClientName { get; set; }
    public string? StrategyTag { get; set; }
    public string? OcoGroupId { get; set; }
    public string? ParentOrderId { get; set; }
    public string BracketRole { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; }
    public string Status { get; set; } = "";
    public decimal FilledQuantity { get; set; }
    public decimal AveragePrice { get; set; }
    public decimal FeesPaid { get; set; }
    public bool IsTriggered { get; set; }
    public string? RejectReason { get; set; }
    public string? RiskRule { get; set; }
    public DateTimeOffset? LastUpdateUtc { get; set; }
}

public sealed class FillEntity
{
    public string FillId { get; set; } = "";
    public string OrderId { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Side { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public decimal Fee { get; set; }
    public string Liquidity { get; set; } = "";
    public string Quality { get; set; } = "";
    public decimal FxRate { get; set; }
    public string? FxSource { get; set; }
    public DateTimeOffset TimestampUtc { get; set; }
    public DateTimeOffset MarketDataAsOfUtc { get; set; }
    public bool ExecutedDuringGap { get; set; }
    public string Source { get; set; } = "";
    public string? StrategyTag { get; set; }
    public decimal RealizedPnl { get; set; }
}

/// <summary>Projektion: Position je Konto und Symbol (FIFO-Lots als JSON). Menge 0 = geschlossen.</summary>
public sealed class PositionEntity
{
    public string AccountId { get; set; } = "";
    public string Symbol { get; set; } = "";
    public decimal Quantity { get; set; }
    /// <summary>Durchschnittlicher Einstand je Stück (Quote-Währung).</summary>
    public decimal AverageCost { get; set; }
    /// <summary>Einstand gesamt in Basiswährung inkl. Kaufgebühren.</summary>
    public decimal CostBasis { get; set; }
    public decimal RealizedPnl { get; set; }
    public decimal? LastPrice { get; set; }
    public DateTimeOffset? LastPriceUtc { get; set; }
    public string LotsJson { get; set; } = "[]";
    public DateTimeOffset UpdatedUtc { get; set; }
}

/// <summary>Kontobuchung (Einzahlung, Kauf, Verkauf, Gebühr …).</summary>
public sealed class CashEntryEntity
{
    public string EntryId { get; set; } = "";
    public string AccountId { get; set; } = "";
    public DateTimeOffset TimestampUtc { get; set; }
    public decimal Amount { get; set; }
    public string Kind { get; set; } = "";
    public string? Reference { get; set; }
    public string? Note { get; set; }
    public long EventSequence { get; set; }
}

public sealed class CandleEntity
{
    public string Symbol { get; set; } = "";
    public string Timeframe { get; set; } = "";
    public DateTimeOffset OpenTimeUtc { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public decimal Volume { get; set; }
    public int TradeCount { get; set; }
    public string Source { get; set; } = "";
    public bool IsBackfill { get; set; }
    public bool IsClosed { get; set; }
}

/// <summary>Protokoll (KI-Aktionen und alle Befehle).</summary>
public sealed class AiActionEntity
{
    public string AuditId { get; set; } = "";
    public DateTimeOffset TimestampUtc { get; set; }
    public string Source { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string Action { get; set; } = "";
    public string? ParametersJson { get; set; }
    public string Result { get; set; } = "";
    public string? Message { get; set; }
    public string? ApprovedBy { get; set; }
    public string? IdempotencyKey { get; set; }
    public double? DurationMs { get; set; }
}

public sealed class SettingEntity
{
    public string Key { get; set; } = "";
    public string ValueJson { get; set; } = "";
    public DateTimeOffset UpdatedUtc { get; set; }
}

/// <summary>Event-Log (Quelle der Wahrheit). Id = globale Einfügereihenfolge.</summary>
public sealed class EventEntity
{
    public long Id { get; set; }
    public string AccountId { get; set; } = "";
    public long Sequence { get; set; }
    public string Type { get; set; } = "";
    public int SchemaVersion { get; set; }
    public DateTimeOffset TimestampUtc { get; set; }
    public string? EventId { get; set; }
    public string Json { get; set; } = "";
}

public sealed class AlertEntity
{
    public string AlertId { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Condition { get; set; } = "";
    public decimal Threshold { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? TriggeredUtc { get; set; }
}
