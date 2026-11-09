using System.Text.Json.Serialization;
using Musterdepot.Core.Domain;

namespace Musterdepot.Core.Events;

/// <summary>
/// Schnittstelle C: versionierte Event-Typen der Engine. Der Depotstand ergibt sich durch Abspielen der Events.
/// Neue Felder nur optional hinzufügen; inkompatible Änderungen erhöhen <see cref="SchemaVersion"/>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(AccountOpened), nameof(AccountOpened))]
[JsonDerivedType(typeof(CashDeposited), nameof(CashDeposited))]
[JsonDerivedType(typeof(CashWithdrawn), nameof(CashWithdrawn))]
[JsonDerivedType(typeof(OrderSubmitted), nameof(OrderSubmitted))]
[JsonDerivedType(typeof(OrderAccepted), nameof(OrderAccepted))]
[JsonDerivedType(typeof(OrderRejected), nameof(OrderRejected))]
[JsonDerivedType(typeof(OrderModified), nameof(OrderModified))]
[JsonDerivedType(typeof(OrderTriggered), nameof(OrderTriggered))]
[JsonDerivedType(typeof(TrailingStopAdjusted), nameof(TrailingStopAdjusted))]
[JsonDerivedType(typeof(OrderFilled), nameof(OrderFilled))]
[JsonDerivedType(typeof(OrderCancelled), nameof(OrderCancelled))]
[JsonDerivedType(typeof(OrderExpired), nameof(OrderExpired))]
[JsonDerivedType(typeof(KillSwitchEngaged), nameof(KillSwitchEngaged))]
[JsonDerivedType(typeof(KillSwitchReleased), nameof(KillSwitchReleased))]
[JsonDerivedType(typeof(DailyLossLimitHit), nameof(DailyLossLimitHit))]
[JsonDerivedType(typeof(RiskConfigChanged), nameof(RiskConfigChanged))]
[JsonDerivedType(typeof(FeeModelChanged), nameof(FeeModelChanged))]
[JsonDerivedType(typeof(MarketPriceMarked), nameof(MarketPriceMarked))]
public abstract record DomainEvent
{
    /// <summary>Fortlaufende Nummer je Konto, von der Engine vergeben.</summary>
    public long Sequence { get; init; }
    public required string AccountId { get; init; }
    public required DateTimeOffset TimestampUtc { get; init; }
    public string EventId { get; init; } = "";
    public virtual int SchemaVersion => 1;
    [JsonIgnore] public string EventType => GetType().Name;
}

public sealed record AccountOpened : DomainEvent
{
    public required string Name { get; init; }
    public string BaseCurrency { get; init; } = "EUR";
    public required decimal InitialCash { get; init; }
    public RiskConfig Risk { get; init; } = RiskConfig.Default;
    public FeeModelConfig CryptoFees { get; init; } = FeeModelConfig.KrakenLike;
    public FeeModelConfig StockFees { get; init; } = FeeModelConfig.NeoBroker;
}

public sealed record CashDeposited : DomainEvent
{
    public required decimal Amount { get; init; }
    public string? Note { get; init; }
}

public sealed record CashWithdrawn : DomainEvent
{
    public required decimal Amount { get; init; }
    public string? Note { get; init; }
}

/// <summary>Order eingegangen (nach bestandenem Risiko-Gate). Enthält die vollständige Order.</summary>
public sealed record OrderSubmitted : DomainEvent
{
    public required Order Order { get; init; }
}

public sealed record OrderAccepted : DomainEvent
{
    public required string OrderId { get; init; }
}

public sealed record OrderRejected : DomainEvent
{
    public required string OrderId { get; init; }
    public required string ClientOrderId { get; init; }
    public required string Symbol { get; init; }
    public required string Reason { get; init; }
    /// <summary>Verletzte Risikoregel, z. B. "MaxOrderNotional", "PriceBand", "KillSwitch".</summary>
    public string? RiskRule { get; init; }
    public CommandSource Source { get; init; }
}

public sealed record OrderModified : DomainEvent
{
    public required string OrderId { get; init; }
    public decimal? NewQuantity { get; init; }
    public decimal? NewLimitPrice { get; init; }
    public decimal? NewStopPrice { get; init; }
    public CommandSource Source { get; init; }
}

/// <summary>Stop/Stop-Limit/Trailing wurde durch den letzten Trade ausgelöst.</summary>
public sealed record OrderTriggered : DomainEvent
{
    public required string OrderId { get; init; }
    public required decimal TriggerPrice { get; init; }
}

public sealed record TrailingStopAdjusted : DomainEvent
{
    public required string OrderId { get; init; }
    public required decimal NewStopPrice { get; init; }
    public required decimal ReferencePrice { get; init; }
}

/// <summary>Teil- oder Vollausführung.</summary>
public sealed record OrderFilled : DomainEvent
{
    public required Fill Fill { get; init; }
    /// <summary>Order-Status nach diesem Fill (PartiallyFilled oder Filled).</summary>
    public required OrderStatus ResultingStatus { get; init; }
    /// <summary>Verwendeter Umrechnungskurs und dessen Quelle (Basiswährung EUR).</summary>
    public string? FxSource { get; init; }
}

public sealed record OrderCancelled : DomainEvent
{
    public required string OrderId { get; init; }
    public required string Reason { get; init; }
    public CommandSource Source { get; init; }
}

public sealed record OrderExpired : DomainEvent
{
    public required string OrderId { get; init; }
}

public sealed record KillSwitchEngaged : DomainEvent
{
    public required CommandSource Source { get; init; }
    public string? ClientName { get; init; }
    public required string Reason { get; init; }
    public bool OrdersCancelled { get; init; } = true;
}

/// <summary>Nur ein Mensch im Programm kann den Not-Aus lösen.</summary>
public sealed record KillSwitchReleased : DomainEvent
{
    public required string ReleasedBy { get; init; }
}

public sealed record DailyLossLimitHit : DomainEvent
{
    public required decimal Loss { get; init; }
    public required decimal Limit { get; init; }
}

public sealed record RiskConfigChanged : DomainEvent
{
    public required RiskConfig Risk { get; init; }
}

public sealed record FeeModelChanged : DomainEvent
{
    public required AssetClass AssetClass { get; init; }
    public required FeeModelConfig Fees { get; init; }
}

/// <summary>Bewertungskurs für eine Position (für Equity-Kurve und Tagesverlust), optional gespeichert.</summary>
public sealed record MarketPriceMarked : DomainEvent
{
    public required string Symbol { get; init; }
    public required decimal Price { get; init; }
}
