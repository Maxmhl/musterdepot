using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;
using Musterdepot.MarketData.Infrastructure;
using Musterdepot.MarketData.Sources;

namespace Musterdepot.MarketData.Coinbase;

public sealed record CoinbaseOptions : WebSocketSourceOptions
{
    public Uri WebSocketUri { get; init; } = new("wss://ws-feed.exchange.coinbase.com");
    public string RestBaseUrl { get; init; } = "https://api.exchange.coinbase.com";
    public bool SubscribeLevel2Batch { get; init; } = true;
    public TimeSpan PageDelay { get; init; } = TimeSpan.FromMilliseconds(150);
    public IReadOnlyList<string> QuoteCurrencies { get; init; } = ["EUR", "USD", "USDC"];

    public CoinbaseOptions()
    {
        // Mit Abos kommt sekündlich ein heartbeat je Produkt; ohne Abos gibt es keinen App-Ping.
        ReceiveTimeout = TimeSpan.FromSeconds(30);
        MaxConnectionsPerWindow = 60;
    }
}

/// <summary>Coinbase Exchange (Ausweichquelle): WS ticker, matches, level2_batch, heartbeat; REST-Kerzen je 300 Stück.</summary>
public sealed class CoinbaseSource : WebSocketMarketDataSource, IHistoryCapabilities
{
    public const string SourceName = "coinbase";

    private readonly CoinbaseOptions _options;
    private readonly CoinbaseRestClient _rest;
    private readonly CoinbaseMessageParser _parser = new(SourceName);
    private readonly ConcurrentDictionary<string, Instrument> _products = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, OrderBookState> _books = new(StringComparer.Ordinal);

    public CoinbaseSource(CoinbaseOptions options, HttpClient http, IWebSocketConnectionFactory factory, IClock clock, IRandomSource random,
        ConnectionRateLimiter? rateLimiter = null, ILogger<CoinbaseSource>? logger = null)
        : base(SourceName, options.WebSocketUri, options, factory, clock, random, rateLimiter, logger)
    {
        _options = options;
        _rest = new CoinbaseRestClient(http, options.RestBaseUrl) { PageDelay = options.PageDelay };
    }

    public override IReadOnlyCollection<AssetClass> AssetClasses { get; } = [AssetClass.Crypto];

    public TimeSpan? GetMaxHistoryLookback(Timeframe timeframe) => null;

    public async Task EnsureProductsAsync(CancellationToken ct)
    {
        if (!_products.IsEmpty) return;
        foreach (var p in await _rest.GetProductsAsync(ct).ConfigureAwait(false)) _products[p.Symbol] = p;
    }

    protected override void OnStarted()
    {
        if (_products.IsEmpty) _ = Task.Run(async () => { try { await EnsureProductsAsync(CancellationToken.None); } catch { /* später */ } });
    }

    public override bool Supports(string symbol)
    {
        if (!_products.IsEmpty) return _products.ContainsKey(symbol);
        return SymbolHelper.IsCryptoPair(symbol) && SymbolHelper.TrySplitPair(symbol, out _, out var q) && _options.QuoteCurrencies.Contains(q);
    }

    public override async Task<IReadOnlyList<Instrument>> SearchSymbolsAsync(string query, CancellationToken ct)
    {
        try { await EnsureProductsAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Logger.LogDebug(ex, "Coinbase-Produkte nicht ladbar"); }
        return FilterInstruments(_products.Values.Where(p => _options.QuoteCurrencies.Contains(p.QuoteCurrency)), query);
    }

    public override Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, Timeframe timeframe, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, CancellationToken ct)
    {
        var now = Clock.UtcNow;
        var to = toUtc ?? now;
        var from = fromUtc ?? to - timeframe.ToTimeSpan() * CoinbaseRestClient.MaxCandlesPerRequest;
        return _rest.GetCandlesAsync(symbol, timeframe, from, to, now, ct);
    }

    public override async Task<OrderBookSnapshot?> GetOrderBookAsync(string symbol, int depth, CancellationToken ct)
    {
        if (_books.TryGetValue(symbol, out var book) && book.HasSnapshot)
            return book.ToSnapshot(symbol, SourceName, depth: depth);
        var (bids, asks) = await _rest.GetBookAsync(symbol, ct).ConfigureAwait(false);
        return new OrderBookSnapshot { Symbol = symbol, Source = SourceName, AsOfUtc = Clock.UtcNow, Bids = bids.Take(depth).ToList(), Asks = asks.Take(depth).ToList() };
    }

    protected override IEnumerable<string> BuildSubscribeMessages(IReadOnlyCollection<string> symbols)
    {
        if (symbols.Count > 0) yield return CoinbaseMessageParser.BuildSubscribe(symbols, _options.SubscribeLevel2Batch);
    }

    protected override IEnumerable<string> BuildUnsubscribeMessages(IReadOnlyCollection<string> symbols)
    {
        if (symbols.Count > 0) yield return CoinbaseMessageParser.BuildSubscribe(symbols, _options.SubscribeLevel2Batch, "unsubscribe");
    }

    protected override void OnConnectionReset() => _books.Clear();

    protected override Task HandleMessageAsync(string message, DateTimeOffset receivedUtc, CancellationToken ct)
    {
        var parsed = _parser.Parse(message, receivedUtc);
        if (parsed.Kind == WsMessageKind.Error) { PublishWarning($"Coinbase meldet Fehler: {parsed.Message}"); return Task.CompletedTask; }
        if (parsed.Kind != WsMessageKind.Data) return Task.CompletedTask;
        foreach (var e in parsed.Events)
        {
            if (e is OrderBookUpdate book)
            {
                var state = _books.GetOrAdd(book.Symbol, _ => new OrderBookState(1000));
                if (!book.IsSnapshot && !state.HasSnapshot) continue;
                state.Apply(book);
            }
            Publish(e);
        }
        return Task.CompletedTask;
    }
}
