using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;
using Musterdepot.MarketData.Infrastructure;
using Musterdepot.MarketData.Sources;

namespace Musterdepot.MarketData.Bitvavo;

public sealed record BitvavoOptions : WebSocketSourceOptions
{
    public Uri WebSocketUri { get; init; } = new("wss://ws.bitvavo.com/v2/");
    public string RestBaseUrl { get; init; } = "https://api.bitvavo.com/v2";
    public bool SubscribeBook { get; init; } = true;
    public int BookDepth { get; init; } = 50;
    public TimeSpan PageDelay { get; init; } = TimeSpan.FromMilliseconds(100);
    /// <summary>Bitvavo-Märkte nur gegen diese Quote-Währungen (EUR-Paare).</summary>
    public IReadOnlyList<string> QuoteCurrencies { get; init; } = ["EUR"];

    public BitvavoOptions()
    {
        // Bitvavo sendet keine Heartbeats; getTime dient als Ping.
        ReceiveTimeout = TimeSpan.FromSeconds(45);
        PingInterval = TimeSpan.FromSeconds(15);
        MaxConnectionsPerWindow = 60;
    }
}

/// <summary>
/// Bitvavo (Historie + Ausweichquelle, EUR-Paare): WS ticker, ticker24h, trades, candles (1m), book (Snapshot via getBook,
/// Deltas mit Nonce-Prüfung). REST: Kerzen mit Paging weit zurück, /markets.
/// </summary>
public sealed class BitvavoSource : WebSocketMarketDataSource, IHistoryCapabilities
{
    public const string SourceName = "bitvavo";

    private readonly BitvavoOptions _options;
    private readonly BitvavoRestClient _rest;
    private readonly BitvavoMessageParser _parser = new(SourceName);
    private readonly ConcurrentDictionary<string, BitvavoMarket> _markets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, OrderBookState> _books = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _nonces = new(StringComparer.Ordinal);
    private readonly CandleStreamTracker _candles = new();

    public BitvavoSource(BitvavoOptions options, HttpClient http, IWebSocketConnectionFactory factory, IClock clock, IRandomSource random,
        ConnectionRateLimiter? rateLimiter = null, ILogger<BitvavoSource>? logger = null)
        : base(SourceName, options.WebSocketUri, options, factory, clock, random, rateLimiter, logger)
    {
        _options = options;
        _rest = new BitvavoRestClient(http, options.RestBaseUrl) { PageDelay = options.PageDelay };
    }

    public override IReadOnlyCollection<AssetClass> AssetClasses { get; } = [AssetClass.Crypto];
    public int NonceResyncs { get; private set; }

    public TimeSpan? GetMaxHistoryLookback(Timeframe timeframe) => null;

    protected override void OnStarted()
    {
        if (_markets.IsEmpty) _ = Task.Run(async () => { try { await EnsureMarketsAsync(CancellationToken.None); } catch { /* später */ } });
    }

    public async Task EnsureMarketsAsync(CancellationToken ct)
    {
        if (!_markets.IsEmpty) return;
        foreach (var m in await _rest.GetMarketsAsync(ct).ConfigureAwait(false)) _markets[m.Symbol] = m;
    }

    public void SetMarkets(IEnumerable<BitvavoMarket> markets)
    {
        foreach (var m in markets) _markets[m.Symbol] = m;
    }

    public override bool Supports(string symbol)
    {
        if (!_markets.IsEmpty) return _markets.ContainsKey(symbol);
        return SymbolHelper.IsCryptoPair(symbol) && SymbolHelper.TrySplitPair(symbol, out _, out var q) && _options.QuoteCurrencies.Contains(q);
    }

    public override async Task<IReadOnlyList<Instrument>> SearchSymbolsAsync(string query, CancellationToken ct)
    {
        try { await EnsureMarketsAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Logger.LogDebug(ex, "Bitvavo-Märkte nicht ladbar"); }
        var all = _markets.Values.Where(m => m.Status == "trading" && _options.QuoteCurrencies.Contains(m.Quote)).Select(m => m.ToInstrument());
        return FilterInstruments(all, query);
    }

    public override Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, Timeframe timeframe, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, CancellationToken ct)
    {
        var now = Clock.UtcNow;
        var to = toUtc ?? now;
        var from = fromUtc ?? to - timeframe.ToTimeSpan() * BitvavoRestClient.MaxCandlesPerRequest;
        return _rest.GetCandlesAsync(symbol, timeframe, from, to, now, ct);
    }

    public override async Task<OrderBookSnapshot?> GetOrderBookAsync(string symbol, int depth, CancellationToken ct)
    {
        if (_books.TryGetValue(symbol, out var book) && book.HasSnapshot)
            return book.ToSnapshot(symbol, SourceName, depth: depth);
        var (bids, asks, _) = await _rest.GetBookAsync(symbol, depth, ct).ConfigureAwait(false);
        return new OrderBookSnapshot { Symbol = symbol, Source = SourceName, AsOfUtc = Clock.UtcNow, Bids = bids, Asks = asks };
    }

    // ---------- WebSocket ----------
    protected override IEnumerable<string> BuildSubscribeMessages(IReadOnlyCollection<string> symbols)
    {
        if (symbols.Count == 0) yield break;
        yield return BitvavoMessageParser.BuildSubscribe(symbols, _options.SubscribeBook);
        if (_options.SubscribeBook)
            foreach (var s in symbols) yield return BitvavoMessageParser.BuildGetBook(s, _options.BookDepth);
    }

    protected override IEnumerable<string> BuildUnsubscribeMessages(IReadOnlyCollection<string> symbols)
    {
        if (symbols.Count == 0) yield break;
        yield return BitvavoMessageParser.BuildSubscribe(symbols, _options.SubscribeBook, "unsubscribe");
    }

    protected override string? BuildPingMessage() => BitvavoMessageParser.BuildGetTime();

    protected override void OnConnectionReset()
    {
        _books.Clear();
        _nonces.Clear();
        _candles.Reset();
    }

    protected override async Task OnUnsubscribedAsync(IReadOnlyList<string> symbols, CancellationToken ct)
    {
        await base.OnUnsubscribedAsync(symbols, ct).ConfigureAwait(false);
        foreach (var s in symbols) { _books.TryRemove(s, out _); _nonces.TryRemove(s, out _); }
    }

    protected override async Task HandleMessageAsync(string message, DateTimeOffset receivedUtc, CancellationToken ct)
    {
        var parsed = _parser.Parse(message, receivedUtc);
        if (parsed.Kind == WsMessageKind.Error) { PublishWarning($"Bitvavo meldet Fehler: {parsed.Message}"); return; }
        if (parsed.Kind != WsMessageKind.Data) return;

        foreach (var e in parsed.Events)
        {
            switch (e)
            {
                case OrderBookUpdate book:
                    if (!await HandleBookAsync(book, parsed.Sequence, ct).ConfigureAwait(false)) continue;
                    Publish(book);
                    break;
                case CandleUpdate cu:
                    foreach (var c in _candles.Update(cu.Candle))
                        Publish(cu with { Candle = c, AsOfUtc = c.IsClosed ? c.OpenTimeUtc + c.Timeframe.ToTimeSpan() : cu.AsOfUtc });
                    break;
                default:
                    if (IsSubscribed(e.Symbol)) Publish(e);
                    break;
            }
        }
    }

    /// <summary>Nonce-Prüfung: Deltas müssen lückenlos auf den Snapshot folgen, sonst neuer Snapshot per getBook.</summary>
    private async Task<bool> HandleBookAsync(OrderBookUpdate book, long? nonce, CancellationToken ct)
    {
        var state = _books.GetOrAdd(book.Symbol, _ => new OrderBookState(Math.Max(_options.BookDepth, 1000)));
        if (book.IsSnapshot)
        {
            state.Apply(book);
            if (nonce is { } n) _nonces[book.Symbol] = n;
            return true;
        }
        if (!state.HasSnapshot) return false; // Snapshot steht noch aus
        if (nonce is { } nn && _nonces.TryGetValue(book.Symbol, out var last))
        {
            if (nn <= last) return false; // bereits im Snapshot enthalten
            if (nn != last + 1)
            {
                NonceResyncs++;
                state.Clear();
                _nonces.TryRemove(book.Symbol, out _);
                PublishWarning($"Orderbuch {book.Symbol}: Nonce-Lücke ({last} → {nn}), lade Snapshot neu");
                await SendIfConnectedAsync(BitvavoMessageParser.BuildGetBook(book.Symbol, _options.BookDepth), ct).ConfigureAwait(false);
                return false;
            }
            _nonces[book.Symbol] = nn;
        }
        state.Apply(book);
        return true;
    }
}
