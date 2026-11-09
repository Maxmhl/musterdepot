using Musterdepot.Core.Domain;
using Musterdepot.Core.Events;
using Musterdepot.Core.MarketData;

namespace Musterdepot.Core.Abstractions;

/// <summary>Befehl an die Engine (bereits durch die Befehlsschicht geprüft/freigegeben).</summary>
public abstract record EngineCommand
{
    public required string AccountId { get; init; }
    public CommandSource Source { get; init; } = CommandSource.Human;
    public string? ClientName { get; init; }
}

public sealed record OpenAccountRequest : EngineCommand
{
    public required string Name { get; init; }
    public required decimal InitialCash { get; init; }
    public string BaseCurrency { get; init; } = "EUR";
    public RiskConfig? Risk { get; init; }
    public FeeModelConfig? CryptoFees { get; init; }
    public FeeModelConfig? StockFees { get; init; }
}

public sealed record DepositCashRequest : EngineCommand
{
    public required decimal Amount { get; init; }
    public string? Note { get; init; }
}

public sealed record PlaceOrderRequest : EngineCommand
{
    public required string ClientOrderId { get; init; }
    public required string Symbol { get; init; }
    public required OrderSide Side { get; init; }
    public required OrderType Type { get; init; }
    /// <summary>Stückzahl. Alternativ <see cref="Notional"/> (Betrag in Basiswährung).</summary>
    public decimal? Quantity { get; init; }
    public decimal? Notional { get; init; }
    public decimal? LimitPrice { get; init; }
    public decimal? StopPrice { get; init; }
    public decimal? TrailAmount { get; init; }
    public decimal? TrailPercent { get; init; }
    public TimeInForce TimeInForce { get; init; } = TimeInForce.Day;
    public DateTimeOffset? ExpiresUtc { get; init; }
    public string? StrategyTag { get; init; }
    /// <summary>Bracket (v1.1): Take-Profit-Limit für die Gegenorder.</summary>
    public decimal? TakeProfitPrice { get; init; }
    /// <summary>Bracket (v1.1): Stop-Loss-Preis für die Gegenorder.</summary>
    public decimal? StopLossPrice { get; init; }
    /// <summary>OCO (v1.1): Orders mit gleicher Gruppe stornieren sich gegenseitig.</summary>
    public string? OcoGroupId { get; init; }
}

public sealed record CancelOrderRequest : EngineCommand
{
    public required string OrderId { get; init; }
    public string Reason { get; init; } = "user";
}

public sealed record ModifyOrderRequest : EngineCommand
{
    public required string OrderId { get; init; }
    public decimal? NewQuantity { get; init; }
    public decimal? NewLimitPrice { get; init; }
    public decimal? NewStopPrice { get; init; }
}

public sealed record EngageKillSwitchRequest : EngineCommand
{
    public required string Reason { get; init; }
}

public sealed record ReleaseKillSwitchRequest : EngineCommand
{
    public required string ReleasedBy { get; init; }
}

public sealed record UpdateRiskConfigRequest : EngineCommand
{
    public required RiskConfig Risk { get; init; }
}

public sealed record UpdateFeeModelRequest : EngineCommand
{
    public required AssetClass AssetClass { get; init; }
    public required FeeModelConfig Fees { get; init; }
}

public sealed record EngineResult
{
    public required bool Accepted { get; init; }
    public IReadOnlyList<DomainEvent> Events { get; init; } = [];
    public string? RejectReason { get; init; }
    public string? RiskRule { get; init; }
    public string? OrderId { get; init; }
    public static EngineResult Reject(string reason, string? rule = null, IReadOnlyList<DomainEvent>? events = null)
        => new() { Accepted = false, RejectReason = reason, RiskRule = rule, Events = events ?? [] };
}

public sealed class EngineEventsEventArgs(IReadOnlyList<DomainEvent> events) : EventArgs
{
    public IReadOnlyList<DomainEvent> Events { get; } = events;
}

/// <summary>
/// Paper-Trading-Engine: reine Bibliothek ohne Oberfläche und Netzwerk. Intern Decide (Befehl/Marktdaten → Events)
/// und Evolve (Events → Zustand). Thread-sicher (ein Schreiber).
/// </summary>
public interface ITradingEngine
{
    /// <summary>Nach jedem Anhängen neuer Events (zum Speichern und für Lesemodelle).</summary>
    event EventHandler<EngineEventsEventArgs>? EventsAppended;

    /// <summary>Zustand durch Abspielen gespeicherter Events wiederherstellen (löst kein EventsAppended aus).</summary>
    void LoadFromEvents(IEnumerable<DomainEvent> events);
    IReadOnlyList<DomainEvent> GetEvents(string accountId, long afterSequence = 0);

    EngineResult Execute(EngineCommand command);
    OrderPreview Preview(PlaceOrderRequest request);

    /// <summary>Marktdaten einspeisen; kann Fills/Trigger erzeugen.</summary>
    void OnMarketEvent(MarketEvent marketEvent);
    /// <summary>Zeitfortschritt (Ablauf DAY-Orders, Latenz-Warteschlange, Tageswechsel).</summary>
    void OnClockTick(DateTimeOffset utcNow);

    bool AccountExists(string accountId);
    IReadOnlyList<Account> GetAccounts();
    PortfolioSnapshot GetPortfolio(string accountId);
    IReadOnlyList<Order> GetOrders(string accountId, bool openOnly = false);
    Order? GetOrder(string accountId, string orderId);
    Order? GetOrderByClientOrderId(string accountId, string clientOrderId);
    IReadOnlyList<Fill> GetFills(string accountId, DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null);
    IReadOnlyList<CashEntry> GetCashEntries(string accountId);
    IReadOnlyList<EquitySnapshot> GetEquityCurve(string accountId);
    PerformanceStats GetPerformance(string accountId, CommandSource? source = null, string? strategyTag = null);
    RiskConfig GetRiskConfig(string accountId);
    FeeModelConfig GetFeeModel(string accountId, AssetClass assetClass);
    bool IsKillSwitchEngaged(string accountId);
}
