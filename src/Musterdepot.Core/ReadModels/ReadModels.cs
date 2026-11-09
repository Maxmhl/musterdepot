using System.ComponentModel;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;

namespace Musterdepot.Core.ReadModels;

/// <summary>
/// Schnittstelle D: beobachtbarer Zustand, an den sich die Oberfläche bindet.
/// Changed wird auf einem Hintergrund-Thread ausgelöst; die Oberfläche marshalt selbst auf den UI-Thread.
/// </summary>
public interface IReadModel<out T> : INotifyPropertyChanged
{
    T Current { get; }
    event EventHandler? Changed;
}

/// <summary>Einfache, thread-sichere Implementierung von <see cref="IReadModel{T}"/>.</summary>
public sealed class ReadModel<T>(T initial) : IReadModel<T>
{
    private T _current = initial;
    private readonly Lock _lock = new();
    public T Current { get { lock (_lock) return _current; } }
    public event EventHandler? Changed;
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Set(T value)
    {
        lock (_lock) _current = value;
        Changed?.Invoke(this, EventArgs.Empty);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Current)));
    }
}

/// <summary>Alle Lesemodelle der Oberfläche.</summary>
public interface IReadModels
{
    IReadModel<PortfolioSnapshot> Portfolio { get; }
    IReadModel<IReadOnlyList<Order>> OpenOrders { get; }
    IReadModel<IReadOnlyList<Order>> AllOrders { get; }
    IReadModel<IReadOnlyList<Fill>> Fills { get; }
    IReadModel<IReadOnlyList<EquitySnapshot>> EquityCurve { get; }
    IReadModel<IReadOnlyDictionary<string, QuoteSnapshot>> Quotes { get; }
    IReadModel<IReadOnlyList<Watchlist>> Watchlists { get; }
    IReadModel<IReadOnlyList<PriceAlert>> Alerts { get; }
    IReadModel<IReadOnlyList<AuditEntry>> AuditLog { get; }
    IReadModel<IReadOnlyList<ApprovalRequest>> PendingApprovals { get; }
    IReadModel<IReadOnlyDictionary<string, ConnectionState>> Connections { get; }
    IReadModel<AppSettings> Settings { get; }
    IReadModel<bool> KillSwitch { get; }
    /// <summary>Feingranulares Kursereignis für Charts (pro Tick).</summary>
    event EventHandler<MarketEvent>? MarketEventReceived;
}

/// <summary>Lesende Abfragen (für MCP-Lese-Tools und Oberfläche). Keine Nebenwirkung.</summary>
public interface ITradingQueries
{
    Task<IReadOnlyList<Instrument>> SearchSymbolsAsync(string query, CancellationToken ct = default);
    QuoteSnapshot? GetQuote(string symbol);
    Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, Timeframe timeframe, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, int? limit, CancellationToken ct = default);
    Task<OrderBookSnapshot?> GetOrderBookAsync(string symbol, int depth, CancellationToken ct = default);
    PortfolioSnapshot GetPortfolio(string? accountId = null);
    IReadOnlyList<PositionSnapshot> GetPositions(string? accountId = null);
    IReadOnlyList<Order> ListOrders(bool openOnly, string? accountId = null);
    IReadOnlyList<Fill> GetTradeHistory(DateTimeOffset? fromUtc, DateTimeOffset? toUtc, string? accountId = null);
    PerformanceStats GetPerformanceStats(CommandSource? source, string? strategyTag, string? accountId = null);
    IReadOnlyList<EquitySnapshot> GetEquityCurve(string? accountId = null);
    Task<IReadOnlyList<Watchlist>> ListWatchlistsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PriceAlert>> ListAlertsAsync(CancellationToken ct = default);
    Task<AppSettings> GetSettingsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AuditEntry>> GetAuditLogAsync(DateTimeOffset? fromUtc, string? clientId, int limit, CancellationToken ct = default);
    AgentStatus GetAgentStatus(string clientId);
}
