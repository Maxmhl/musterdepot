using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;
using Musterdepot.MarketData.Infrastructure;
using Musterdepot.MarketData.Sources;

namespace Musterdepot.MarketData.Kraken;

public sealed record KrakenOptions : WebSocketSourceOptions
{
    public Uri WebSocketUri { get; init; } = new("wss://ws.kraken.com/v2");
    public string RestBaseUrl { get; init; } = "https://api.kraken.com";
    /// <summary>Orderbuchtiefe: 10, 25, 100, 500 oder 1000.</summary>
    public int BookDepth { get; init; } = 10;
    public bool SubscribeTicker { get; init; } = true;
    public bool SubscribeTrades { get; init; } = true;
    public bool SubscribeOhlc { get; init; } = true;
    public bool SubscribeBook { get; init; } = true;
    /// <summary>CRC32-Prüfsumme des Orderbuchs prüfen (nur wenn die Präzision aus AssetPairs bekannt ist).</summary>
    public bool VerifyBookChecksum { get; init; } = true;
    /// <summary>Zusätzlich gültige Quote-Währungen, solange AssetPairs nicht geladen sind.</summary>
    public IReadOnlyList<string> KnownQuoteCurrencies { get; init; } = ["EUR", "USD", "USDC", "USDT", "GBP", "CHF", "BTC", "ETH"];

    public KrakenOptions()
    {
        // Kraken sendet bei Abos etwa sekündlich Heartbeats; Ping hält die Verbindung auch ohne Abos lebendig.
        ReceiveTimeout = TimeSpan.FromSeconds(30);
        PingInterval = TimeSpan.FromSeconds(20);
        MaxConnectionsPerWindow = 100;
        ConnectionWindow = TimeSpan.FromMinutes(10);
    }
}

/// <summary>
/// Kraken WebSocket v2 (Hauptquelle Krypto, Echtzeit): ticker, trade, ohlc (1 min), book (10…1000 Stufen, Snapshot+Delta,
/// CRC32-Prüfung). REST: OHLC (max. 720 Kerzen), AssetPairs für die Symbolsuche, Depth.
/// </summary>
public sealed class KrakenSource : WebSocketMarketDataSource, IHistoryCapabilities
{
    public const string SourceName = "kraken";
    private static readonly int[] AllowedDepths = [10, 25, 100, 500, 1000];

    private readonly KrakenOptions _options;
    private readonly KrakenRestClient _rest;
    private readonly KrakenMessageParser _parser = new(SourceName);
    private readonly ConcurrentDictionary<string, KrakenPairInfo> _pairs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, OrderBookState> _books = new(StringComparer.Ordinal);
    private readonly CandleStreamTracker _candles = new();
    private readonly SemaphoreSlim _pairsGate = new(1, 1);
    private DateTimeOffset _pairsLoadedUtc = DateTimeOffset.MinValue;

    public KrakenSource(KrakenOptions options, HttpClient http, IWebSocketConnectionFactory factory, IClock clock, IRandomSource random,
        ConnectionRateLimiter? rateLimiter = null, ILogger<KrakenSource>? logger = null)
        : base(SourceName, options.WebSocketUri, options, factory, clock, random, rateLimiter, logger)
    {
        _options = options;
        _rest = new KrakenRestClient(http, options.RestBaseUrl);
        BookDepth = AllowedDepths.FirstOrDefault(d => d >= options.BookDepth, 1000);
    }

    public override IReadOnlyCollection<AssetClass> AssetClasses { get; } = [AssetClass.Crypto];
    public int BookDepth { get; }
    public int ChecksumMismatches { get; private set; }
    public bool PairsLoaded => !_pairs.IsEmpty;

    public TimeSpan? GetMaxHistoryLookback(Timeframe timeframe) => timeframe.ToTimeSpan() * KrakenRestClient.MaxOhlcCandles;

    protected override void OnStarted()
    {
        if (_pairs.IsEmpty) _ = Task.Run(async () => { try { await EnsurePairsAsync(CancellationToken.None); } catch { /* später erneut */ } });
    }

    /// <summary>Lädt AssetPairs (gecacht, 24 h).</summary>
    public async Task EnsurePairsAsync(CancellationToken ct)
    {
        if (!_pairs.IsEmpty && Clock.UtcNow - _pairsLoadedUtc < TimeSpan.FromHours(24)) return;
        await _pairsGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_pairs.IsEmpty && Clock.UtcNow - _pairsLoadedUtc < TimeSpan.FromHours(24)) return;
            var pairs = await _rest.GetAssetPairsAsync(ct).ConfigureAwait(false);
            foreach (var p in pairs) _pairs[p.Symbol] = p;
            _pairsLoadedUtc = Clock.UtcNow;
        }
        finally { _pairsGate.Release(); }
    }

    /// <summary>Paare direkt setzen (Tests, Offline-Betrieb).</summary>
    public void SetPairs(IEnumerable<KrakenPairInfo> pairs)
    {
        foreach (var p in pairs) _pairs[p.Symbol] = p;
        _pairsLoadedUtc = Clock.UtcNow;
    }

    public override bool Supports(string symbol)
    {
        if (!_pairs.IsEmpty) return _pairs.ContainsKey(symbol);
        return SymbolHelper.TrySplitPair(symbol, out _, out var q) && SymbolHelper.IsCryptoPair(symbol) && _options.KnownQuoteCurrencies.Contains(q);
    }

    public override async Task<IReadOnlyList<Instrument>> SearchSymbolsAsync(string query, CancellationToken ct)
    {
        try { await EnsurePairsAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Logger.LogDebug(ex, "Kraken AssetPairs nicht ladbar"); }
        var all = _pairs.Values.Where(p => p.Status == "online").Select(p => p.ToInstrument());
        return FilterInstruments(all, query);
    }

    private string RestPair(string symbol) => _pairs.TryGetValue(symbol, out var p) ? p.AltName : SymbolHelper.ToKrakenRestPair(symbol);

    public override async Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, Timeframe timeframe, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, CancellationToken ct)
    {
        var now = Clock.UtcNow;
        var candles = await _rest.GetOhlcAsync(symbol, RestPair(symbol), timeframe, fromUtc, now, ct).ConfigureAwait(false);
        return candles.Where(c => (fromUtc is null || c.OpenTimeUtc >= timeframe.Floor(fromUtc.Value)) && (toUtc is null || c.OpenTimeUtc < toUtc.Value)).ToList();
    }

    public override async Task<OrderBookSnapshot?> GetOrderBookAsync(string symbol, int depth, CancellationToken ct)
    {
        if (_books.TryGetValue(symbol, out var book) && book.HasSnapshot)
            return book.ToSnapshot(symbol, SourceName, depth: depth);
        var (bids, asks) = await _rest.GetDepthAsync(RestPair(symbol), Math.Clamp(depth, 1, 500), ct).ConfigureAwait(false);
        return new OrderBookSnapshot { Symbol = symbol, Source = SourceName, AsOfUtc = Clock.UtcNow, Bids = bids, Asks = asks };
    }

    // ---------- WebSocket ----------
    protected override IEnumerable<string> BuildSubscribeMessages(IReadOnlyCollection<string> symbols) => BuildMessages(symbols, "subscribe");
    protected override IEnumerable<string> BuildUnsubscribeMessages(IReadOnlyCollection<string> symbols) => BuildMessages(symbols, "unsubscribe");

    private IEnumerable<string> BuildMessages(IReadOnlyCollection<string> symbols, string method)
    {
        if (symbols.Count == 0) yield break;
        if (_options.SubscribeTicker) yield return KrakenMessageParser.BuildSubscribe("ticker", symbols, method: method);
        if (_options.SubscribeTrades) yield return KrakenMessageParser.BuildSubscribe("trade", symbols, snapshot: method == "subscribe" ? false : null, method: method);
        if (_options.SubscribeOhlc) yield return KrakenMessageParser.BuildSubscribe("ohlc", symbols, interval: 1, method: method);
        if (_options.SubscribeBook) yield return KrakenMessageParser.BuildSubscribe("book", symbols, depth: BookDepth, method: method);
    }

    protected override string? BuildPingMessage() => KrakenMessageParser.BuildPing();

    protected override void OnConnectionReset()
    {
        _books.Clear();
        _candles.Reset();
    }

    protected override async Task OnUnsubscribedAsync(IReadOnlyList<string> symbols, CancellationToken ct)
    {
        await base.OnUnsubscribedAsync(symbols, ct).ConfigureAwait(false);
        foreach (var s in symbols) _books.TryRemove(s, out _);
    }

    protected override async Task HandleMessageAsync(string message, DateTimeOffset receivedUtc, CancellationToken ct)
    {
        var parsed = _parser.Parse(message, receivedUtc);
        switch (parsed.Kind)
        {
            case WsMessageKind.Error:
                PublishWarning($"Kraken meldet Fehler: {parsed.Message}");
                return;
            case WsMessageKind.Status when parsed.Message is { } system && system != "online":
                PublishWarning($"Kraken-Systemstatus: {system}");
                return;
            case WsMessageKind.Data:
                break;
            default:
                return;
        }

        var resync = new List<string>();
        foreach (var e in parsed.Events)
        {
            switch (e)
            {
                case OrderBookUpdate book:
                    var state = _books.GetOrAdd(book.Symbol, _ => new OrderBookState(BookDepth));
                    if (!book.IsSnapshot && !state.HasSnapshot) continue; // Delta vor Snapshot verwerfen
                    state.Apply(book);
                    Publish(book);
                    if (!VerifyChecksum(book, state)) resync.Add(book.Symbol);
                    break;
                case CandleUpdate cu:
                    foreach (var c in _candles.Update(cu.Candle))
                        Publish(cu with { Candle = c, AsOfUtc = c.IsClosed && !cu.Candle.IsClosed ? c.OpenTimeUtc + c.Timeframe.ToTimeSpan() : cu.AsOfUtc });
                    break;
                default:
                    Publish(e);
                    break;
            }
        }

        if (resync.Count > 0)
        {
            ChecksumMismatches += resync.Count;
            PublishWarning($"Orderbuch-Prüfsumme falsch für {string.Join(", ", resync)} – neu abonniert");
            foreach (var s in resync) _books.TryRemove(s, out _);
            await SendIfConnectedAsync(KrakenMessageParser.BuildSubscribe("book", resync, depth: BookDepth, method: "unsubscribe"), ct).ConfigureAwait(false);
            await SendIfConnectedAsync(KrakenMessageParser.BuildSubscribe("book", resync, depth: BookDepth), ct).ConfigureAwait(false);
        }
    }

    private bool VerifyChecksum(OrderBookUpdate book, OrderBookState state)
    {
        if (!_options.VerifyBookChecksum || book.Checksum is not { } expected) return true;
        if (!_pairs.TryGetValue(book.Symbol, out var pair)) return true;
        var actual = KrakenChecksum.Compute(state.TopAsks(10), state.TopBids(10), pair.PricePrecision, pair.QtyPrecision);
        return actual == (uint)expected;
    }
}
