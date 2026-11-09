namespace Musterdepot.Core.Domain;

/// <summary>Order-Zustand (unveränderlich, Änderungen per <c>with</c>).</summary>
public sealed record Order
{
    public required string OrderId { get; init; }
    /// <summary>Vom Aufrufer gesetzt, verhindert Doppelorders (entspricht idempotency_key).</summary>
    public required string ClientOrderId { get; init; }
    public required string AccountId { get; init; }
    public required string Symbol { get; init; }
    public required OrderSide Side { get; init; }
    public required OrderType Type { get; init; }
    public TimeInForce TimeInForce { get; init; } = TimeInForce.Day;
    public required decimal Quantity { get; init; }
    public decimal? LimitPrice { get; init; }
    public decimal? StopPrice { get; init; }
    /// <summary>Trailing-Abstand absolut (Preiseinheiten).</summary>
    public decimal? TrailAmount { get; init; }
    /// <summary>Trailing-Abstand in Prozent (z. B. 2 = 2 %).</summary>
    public decimal? TrailPercent { get; init; }
    public DateTimeOffset? ExpiresUtc { get; init; }
    public required CommandSource Source { get; init; }
    /// <summary>Name des KI-Clients oder "ui".</summary>
    public string? ClientName { get; init; }
    public string? StrategyTag { get; init; }
    /// <summary>OCO-Gruppe: wird eine Order gefüllt, werden die anderen storniert.</summary>
    public string? OcoGroupId { get; init; }
    /// <summary>Bracket: Kind-Orders werden erst aktiv, wenn der Einstieg gefüllt ist.</summary>
    public string? ParentOrderId { get; init; }
    public BracketRole BracketRole { get; init; } = BracketRole.None;
    public required DateTimeOffset CreatedUtc { get; init; }
    /// <summary>Order wird erst gegen Marktdaten ab diesem Zeitpunkt geprüft (Latenz 200 ms, Verzögerung bei Aktien).</summary>
    public DateTimeOffset EligibleFromUtc { get; init; }
    public OrderStatus Status { get; init; } = OrderStatus.PendingNew;
    public decimal FilledQuantity { get; init; }
    public decimal AveragePrice { get; init; }
    public decimal FeesPaid { get; init; }
    public bool IsTriggered { get; init; }
    /// <summary>Höchst-/Tiefstkurs seit Aktivierung (Trailing-Stop).</summary>
    public decimal? TrailReferencePrice { get; init; }
    public string? RejectReason { get; init; }
    public DateTimeOffset? LastUpdateUtc { get; init; }

    public decimal RemainingQuantity => Quantity - FilledQuantity;
    public bool IsOpen => Status is OrderStatus.PendingNew or OrderStatus.Working or OrderStatus.Triggered or OrderStatus.PartiallyFilled;
}

public sealed record Fill
{
    public required string FillId { get; init; }
    public required string OrderId { get; init; }
    public required string AccountId { get; init; }
    public required string Symbol { get; init; }
    public required OrderSide Side { get; init; }
    public required decimal Quantity { get; init; }
    /// <summary>Preis in Quote-Währung des Instruments.</summary>
    public required decimal Price { get; init; }
    /// <summary>Gebühr in Basiswährung des Kontos (EUR).</summary>
    public required decimal Fee { get; init; }
    public Liquidity Liquidity { get; init; } = Liquidity.Taker;
    public FillQuality Quality { get; init; } = FillQuality.Trade;
    /// <summary>Umrechnungskurs Quote-Währung → Basiswährung, der für die Buchung verwendet wurde (1 bei EUR).</summary>
    public decimal FxRate { get; init; } = 1m;
    public required DateTimeOffset TimestampUtc { get; init; }
    /// <summary>Zeitstempel der Marktdaten, gegen die gefüllt wurde.</summary>
    public DateTimeOffset MarketDataAsOfUtc { get; init; }
    /// <summary>"während Lücke ausgeführt" (nachgeladene Kerzen nach Standby).</summary>
    public bool ExecutedDuringGap { get; init; }
    public CommandSource Source { get; init; }
    public string? StrategyTag { get; init; }
    /// <summary>Realisierter Gewinn/Verlust (EUR, nach FIFO) bei Verkäufen, sonst 0.</summary>
    public decimal RealizedPnl { get; init; }
}

/// <summary>FIFO-Einstandsposten.</summary>
public sealed record Lot
{
    public required string LotId { get; init; }
    public required string Symbol { get; init; }
    public required decimal Quantity { get; init; }
    /// <summary>Einstandspreis je Stück in Quote-Währung.</summary>
    public required decimal Price { get; init; }
    /// <summary>Einstand je Stück in Basiswährung inkl. anteiliger Kaufgebühr.</summary>
    public required decimal CostPerUnitBase { get; init; }
    public required DateTimeOffset OpenedUtc { get; init; }
    public string? OpeningFillId { get; init; }
    public CommandSource Source { get; init; }
    public string? StrategyTag { get; init; }
}

public sealed record CashEntry
{
    public required string EntryId { get; init; }
    public required string AccountId { get; init; }
    public required DateTimeOffset TimestampUtc { get; init; }
    /// <summary>Positiv = Zufluss, negativ = Abfluss (Basiswährung).</summary>
    public required decimal Amount { get; init; }
    /// <summary>z. B. "deposit", "withdrawal", "buy", "sell", "fee".</summary>
    public required string Kind { get; init; }
    public string? Reference { get; init; }
    public string? Note { get; init; }
}

public sealed record Account
{
    public required string AccountId { get; init; }
    public required string Name { get; init; }
    public string BaseCurrency { get; init; } = "EUR";
    public required DateTimeOffset CreatedUtc { get; init; }
    public decimal InitialCash { get; init; }
}

public sealed record EquitySnapshot(DateTimeOffset TimestampUtc, decimal Cash, decimal PositionsValue, decimal Equity);
