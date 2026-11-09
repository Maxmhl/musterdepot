using System.Collections.Concurrent;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Commands;
using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;
using Musterdepot.Core.ReadModels;

namespace Musterdepot.Mcp.Tests;

public sealed class FakeCommandBus : ICommandBus
{
    public ConcurrentQueue<ICommand> Received { get; } = new();
    public Func<ICommand, CommandResult>? Handler { get; set; }

    public Task<CommandResult> SendAsync(ICommand command, CancellationToken ct = default)
    {
        Received.Enqueue(command);
        if (Handler is { } h) return Task.FromResult(h(command));
        return Task.FromResult(command switch
        {
            PlaceOrderCommand { DryRun: true } p => new CommandResult
            {
                Status = CommandStatus.Previewed,
                Data = new OrderPreview { WouldBeAccepted = true, Quantity = p.Quantity ?? 0.01m, EstimatedPrice = 60_000m, EstimatedNotional = 600m, EstimatedFee = 2.4m },
            },
            PlaceOrderCommand p => new CommandResult
            {
                Status = CommandStatus.Executed,
                AuditId = "aud_bus",
                Data = new Order
                {
                    OrderId = "ord_1", ClientOrderId = p.IdempotencyKey, AccountId = "main", Symbol = p.Symbol, Side = p.Side, Type = p.Type,
                    Quantity = p.Quantity ?? 0m, LimitPrice = p.LimitPrice, Source = p.Source, ClientName = p.ClientId, StrategyTag = p.StrategyTag,
                    CreatedUtc = FakeQueries.Now, Status = OrderStatus.Working,
                },
            },
            _ => CommandResult.Ok(null, "ok"),
        });
    }
}

public sealed class FakeQueries : ITradingQueries
{
    public static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    public AppSettings Settings { get; set; } = new();

    public static readonly Instrument Btc = new() { Symbol = "BTC/EUR", Name = "Bitcoin", AssetClass = AssetClass.Crypto, QuoteCurrency = "EUR", BaseAsset = "BTC", PrimarySource = "kraken" };
    public static readonly Instrument Sap = new() { Symbol = "DE0007164600", Name = "SAP SE", AssetClass = AssetClass.Stock, QuoteCurrency = "EUR", Isin = "DE0007164600", PrimarySource = "gettex" };

    public Task<IReadOnlyList<Instrument>> SearchSymbolsAsync(string query, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Instrument>>(new[] { Btc, Sap }
            .Where(i => i.Symbol.Contains(query, StringComparison.OrdinalIgnoreCase) || i.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList());

    public QuoteSnapshot? GetQuote(string symbol) => symbol switch
    {
        "BTC/EUR" => new QuoteSnapshot { Symbol = "BTC/EUR", Source = "kraken", AsOfUtc = Now, DelayedMinutes = 0, IsRealtime = true, Quality = DataQuality.Realtime, Bid = 59_990m, Ask = 60_010m, Last = 60_000m },
        "DE0007164600" => new QuoteSnapshot { Symbol = "DE0007164600", Source = "gettex", AsOfUtc = Now.AddMinutes(-15), DelayedMinutes = 15, IsRealtime = false, Quality = DataQuality.Delayed, Last = 210.5m },
        _ => null,
    };

    public Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, Timeframe timeframe, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, int? limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Candle>>(Enumerable.Range(0, 5).Select(i => new Candle
        {
            Symbol = symbol, Timeframe = timeframe, OpenTimeUtc = Now.AddMinutes(i - 5), Open = 100 + i, High = 101 + i, Low = 99 + i, Close = 100.5m + i, Volume = 1, Source = "kraken",
        }).ToList());

    public Task<OrderBookSnapshot?> GetOrderBookAsync(string symbol, int depth, CancellationToken ct = default) =>
        Task.FromResult<OrderBookSnapshot?>(symbol == "BTC/EUR"
            ? new OrderBookSnapshot { Symbol = symbol, Source = "kraken", AsOfUtc = Now, Bids = [new(59_990m, 1m)], Asks = [new(60_010m, 2m)] }
            : null);

    public PortfolioSnapshot GetPortfolio(string? accountId = null) => new()
    {
        AccountId = "main", AsOfUtc = Now, Cash = 99_000m, PositionsValue = 600m,
        Positions = GetPositions(),
    };

    public IReadOnlyList<PositionSnapshot> GetPositions(string? accountId = null) =>
        [new PositionSnapshot { Symbol = "BTC/EUR", Quantity = 0.01m, AverageCost = 59_000m, CostBasis = 591m, LastPrice = 60_000m, MarketValue = 600m }];

    public IReadOnlyList<Order> ListOrders(bool openOnly, string? accountId = null) => [];
    public IReadOnlyList<Fill> GetTradeHistory(DateTimeOffset? fromUtc, DateTimeOffset? toUtc, string? accountId = null) => [];
    public PerformanceStats GetPerformanceStats(CommandSource? source, string? strategyTag, string? accountId = null) => new() { Source = source, StrategyTag = strategyTag };
    public IReadOnlyList<EquitySnapshot> GetEquityCurve(string? accountId = null) => [];
    public Task<IReadOnlyList<Watchlist>> ListWatchlistsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Watchlist>>([new Watchlist { WatchlistId = "wl_1", Name = "Krypto", Symbols = ["BTC/EUR"] }]);
    public Task<IReadOnlyList<PriceAlert>> ListAlertsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PriceAlert>>([]);
    public Task<AppSettings> GetSettingsAsync(CancellationToken ct = default) => Task.FromResult(Settings);
    public Task<IReadOnlyList<AuditEntry>> GetAuditLogAsync(DateTimeOffset? fromUtc, string? clientId, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AuditEntry>>([]);
    public AgentStatus GetAgentStatus(string clientId) =>
        new() { ClientId = clientId, ClientName = clientId, Mode = ClientMode.Autonomous, Scopes = ClientScopes.All, MaxOrdersPerMinute = 10 };
}

public sealed class InMemorySettingsStore : ISettingsStore
{
    private AppSettings _settings = new();
    private readonly Lock _lock = new();
    public Task<AppSettings> LoadAsync(CancellationToken ct = default) { lock (_lock) return Task.FromResult(_settings); }
    public Task SaveAsync(AppSettings settings, CancellationToken ct = default) { lock (_lock) _settings = settings; return Task.CompletedTask; }
}

public sealed class InMemoryAuditLog : IAuditLog
{
    public ConcurrentQueue<AuditEntry> Entries { get; } = new();
    public event EventHandler<AuditEntry>? EntryAppended;

    public Task<AuditEntry> AppendAsync(AuditEntry entry, CancellationToken ct = default)
    {
        Entries.Enqueue(entry);
        EntryAppended?.Invoke(this, entry);
        return Task.FromResult(entry);
    }

    public Task<IReadOnlyList<AuditEntry>> QueryAsync(DateTimeOffset? fromUtc = null, string? clientId = null, int limit = 200, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AuditEntry>>(Entries.Where(e => clientId is null || e.ClientId == clientId).Take(limit).ToList());
}
