using Microsoft.Extensions.Logging;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;
using Musterdepot.Core.ReadModels;

namespace Musterdepot.Commands;

/// <summary>Lesende Abfragen ohne Nebenwirkung (MCP-Lese-Tools und Oberfläche).</summary>
public sealed class TradingQueries(
    ITradingEngine engine,
    IMarketDataHub hub,
    ICandleStore candles,
    IWatchlistStore watchlists,
    IAlertStore alerts,
    IAuditLog audit,
    IInstrumentStore instruments,
    IApprovalService approvals,
    ClientRegistry clients,
    SettingsService settings,
    ClientActivityTracker activity,
    IClock clock,
    CommandOptions options,
    ILogger<TradingQueries> logger) : ITradingQueries
{
    public async Task<IReadOnlyList<Instrument>> SearchSymbolsAsync(string query, CancellationToken ct = default)
    {
        query = (query ?? "").Trim();
        var result = new List<Instrument>();
        try { result.AddRange(await hub.SearchSymbolsAsync(query, ct).ConfigureAwait(false)); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Symbolsuche über Marktdaten fehlgeschlagen."); }
        try
        {
            var stored = await instruments.GetAllAsync(ct).ConfigureAwait(false);
            result.AddRange(stored.Where(i => Matches(i, query)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Symbolsuche im Instrumentenspeicher fehlgeschlagen."); }
        return result.DistinctBy(i => i.Symbol, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool Matches(Instrument i, string q) =>
        q.Length == 0
        || i.Symbol.Contains(q, StringComparison.OrdinalIgnoreCase)
        || i.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
        || (i.Isin?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
        || (i.Wkn?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
        || (i.BaseAsset?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);

    public QuoteSnapshot? GetQuote(string symbol)
    {
        try { return hub.GetLatestQuote(symbol); }
        catch (Exception ex) { logger.LogWarning(ex, "Kurs {Symbol} nicht lesbar.", symbol); return null; }
    }

    /// <summary>Kerzen bevorzugt aus dem Hub (Quelle/Cache), sonst aus dem Kerzenspeicher. Liefert die letzten <paramref name="limit"/> Kerzen aufsteigend.</summary>
    public async Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, Timeframe timeframe, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, int? limit, CancellationToken ct = default)
    {
        var take = Math.Clamp(limit ?? options.DefaultCandleLimit, 1, options.MaxCandleLimit);
        IReadOnlyList<Candle> list = [];
        try { list = await hub.GetCandlesAsync(symbol, timeframe, fromUtc, toUtc, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Kerzen {Symbol} über Marktdaten fehlgeschlagen.", symbol); }
        if (list.Count == 0)
            list = await candles.GetAsync(symbol, timeframe, fromUtc, toUtc, ct).ConfigureAwait(false);
        return list
            .Where(c => (fromUtc is null || c.OpenTimeUtc >= fromUtc) && (toUtc is null || c.OpenTimeUtc <= toUtc))
            .OrderBy(c => c.OpenTimeUtc)
            .TakeLast(take)
            .ToList();
    }

    public async Task<OrderBookSnapshot?> GetOrderBookAsync(string symbol, int depth, CancellationToken ct = default)
    {
        depth = Math.Clamp(depth, 1, options.MaxOrderBookDepth);
        OrderBookSnapshot? book = null;
        try { book = await hub.GetOrderBookAsync(symbol, depth, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Orderbuch {Symbol} fehlgeschlagen.", symbol); }
        book ??= hub.GetLatestOrderBook(symbol);
        if (book is null) return null;
        return book with { Bids = book.Bids.Take(depth).ToList(), Asks = book.Asks.Take(depth).ToList() };
    }

    private string Account(string? accountId) => string.IsNullOrWhiteSpace(accountId) ? settings.Current.DefaultAccountId : accountId;

    public PortfolioSnapshot GetPortfolio(string? accountId = null)
    {
        var acc = Account(accountId);
        return engine.AccountExists(acc) ? engine.GetPortfolio(acc) : EmptyPortfolio(acc, clock.UtcNow);
    }

    internal static PortfolioSnapshot EmptyPortfolio(string accountId, DateTimeOffset now) =>
        new() { AccountId = accountId, AsOfUtc = now, Cash = 0, PositionsValue = 0, Positions = [] };

    public IReadOnlyList<PositionSnapshot> GetPositions(string? accountId = null) => GetPortfolio(accountId).Positions;

    public IReadOnlyList<Order> ListOrders(bool openOnly, string? accountId = null)
    {
        var acc = Account(accountId);
        return engine.AccountExists(acc) ? engine.GetOrders(acc, openOnly) : [];
    }

    public IReadOnlyList<Fill> GetTradeHistory(DateTimeOffset? fromUtc, DateTimeOffset? toUtc, string? accountId = null)
    {
        var acc = Account(accountId);
        return engine.AccountExists(acc) ? engine.GetFills(acc, fromUtc, toUtc) : [];
    }

    public PerformanceStats GetPerformanceStats(CommandSource? source, string? strategyTag, string? accountId = null)
    {
        var acc = Account(accountId);
        return engine.AccountExists(acc) ? engine.GetPerformance(acc, source, strategyTag) : new PerformanceStats { Source = source, StrategyTag = strategyTag };
    }

    public IReadOnlyList<EquitySnapshot> GetEquityCurve(string? accountId = null)
    {
        var acc = Account(accountId);
        return engine.AccountExists(acc) ? engine.GetEquityCurve(acc) : [];
    }

    public Task<IReadOnlyList<Watchlist>> ListWatchlistsAsync(CancellationToken ct = default) => watchlists.GetAllAsync(ct);

    public Task<IReadOnlyList<PriceAlert>> ListAlertsAsync(CancellationToken ct = default) => alerts.GetAllAsync(ct);

    public Task<AppSettings> GetSettingsAsync(CancellationToken ct = default) => Task.FromResult(settings.Current);

    public Task<IReadOnlyList<AuditEntry>> GetAuditLogAsync(DateTimeOffset? fromUtc, string? clientId, int limit, CancellationToken ct = default)
        => audit.QueryAsync(fromUtc, clientId, Math.Clamp(limit, 1, 1000), ct);

    public AgentStatus GetAgentStatus(string clientId)
    {
        var acc = settings.Current.DefaultAccountId;
        var exists = engine.AccountExists(acc);
        var risk = exists ? engine.GetRiskConfig(acc) : settings.Current.Risk;
        var portfolio = exists ? engine.GetPortfolio(acc) : null;
        var identity = clients.GetIdentity(clientId);
        var isUi = clientId == ClientRegistry.UiClientId;
        var dailyLoss = portfolio?.DailyLossLimitHit ?? false;
        var mode = identity?.Mode ?? ClientMode.ReadOnly;
        if (!isUi && dailyLoss) mode = ClientMode.ReadOnly;
        return new AgentStatus
        {
            ClientId = clientId,
            ClientName = identity?.Name ?? clientId,
            Mode = mode,
            Scopes = identity?.Scopes ?? ClientScopes.None,
            KillSwitchEngaged = exists && engine.IsKillSwitchEngaged(acc),
            DailyLossLimitHit = dailyLoss,
            OrdersLastMinute = activity.OrdersLastMinute(clientId),
            MaxOrdersPerMinute = isUi ? risk.MaxOrdersPerMinuteHuman : risk.MaxOrdersPerMinuteAi,
            PendingApprovals = approvals.Pending.Count(p => p.ClientId == clientId),
            Limits = risk,
            LastActivityUtc = activity.LastActivity(clientId),
        };
    }
}
