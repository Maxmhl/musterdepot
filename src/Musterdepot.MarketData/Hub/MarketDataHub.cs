using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;
using Musterdepot.MarketData.Gettex;
using Musterdepot.MarketData.Infrastructure;
using Musterdepot.MarketData.Sources;

namespace Musterdepot.MarketData.Hub;

public sealed record MarketDataHubOptions
{
    /// <summary>Reihenfolge für Krypto (Kraken bevorzugt, dann Bitvavo, dann Coinbase).</summary>
    public IReadOnlyList<string> CryptoSourcePriority { get; init; } = ["kraken", "bitvavo", "coinbase", "fake", "replay"];
    /// <summary>Reihenfolge für Aktien/ETFs.</summary>
    public IReadOnlyList<string> StockSourcePriority { get; init; } = ["gettex", "gettex-replay", "fake", "replay"];
    /// <summary>Für lange Zeiträume (über das Limit der bevorzugten Quelle hinaus): Bitvavo zuerst.</summary>
    public IReadOnlyList<string> LongHistorySourcePriority { get; init; } = ["bitvavo", "coinbase", "kraken"];
    /// <summary>Aus Trades gebildete Zeitrahmen.</summary>
    public IReadOnlyList<Timeframe> AggregatedTimeframes { get; init; } = [Timeframe.M1, Timeframe.M5, Timeframe.M15, Timeframe.M30, Timeframe.H1, Timeframe.H4, Timeframe.D1];
    /// <summary>Zwischenstände offener Kerzen höchstens so oft melden.</summary>
    public TimeSpan OpenCandleUpdateInterval { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>Karenz für späte Trades, bevor eine Kerze per Zeitablauf geschlossen wird.</summary>
    public TimeSpan CandleCloseGrace { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>So lange darf eine Quelle neu verbinden, bevor auf die nächste umgeschaltet wird (Failed/Disconnected sofort).</summary>
    public TimeSpan FailoverGracePeriod { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>Kerzen-Ereignisse der Quellen zusätzlich weiterreichen (sonst nur aus Trades gebildete und nachgeladene).</summary>
    public bool ForwardSourceCandles { get; init; }
    public int MaxOrderBookDepth { get; init; } = 1000;
    /// <summary>Takt für Kerzenabschluss und Routing-Prüfung (TimeSpan.Zero = kein Timer, nur <see cref="MarketDataHub.RunMaintenanceAsync"/>).</summary>
    public TimeSpan MaintenanceInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan SearchTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Bündelt alle Quellen: Routing Symbol → Quelle nach Priorität, automatisches Failover/Failback, Cache der letzten Kurse
/// (<see cref="QuoteSnapshot"/>) und gepflegter Orderbücher, Kerzen aus Trades (M1 → höhere Zeitrahmen) mit
/// <see cref="CandleUpdate"/>, Historie (Kraken für kurz, Bitvavo für lang). Thread-sicher.
/// </summary>
public sealed class MarketDataHub : IMarketDataHub, IAsyncDisposable
{
    private readonly IReadOnlyList<IMarketDataSource> _sources;
    private readonly IClock _clock;
    private readonly MarketDataHubOptions _options;
    private readonly ILogger _logger;
    private readonly Dictionary<IMarketDataSource, EventHandler<MarketEvent>> _handlers = new(ReferenceEqualityComparer.Instance);

    private readonly Lock _routeLock = new();
    private readonly HashSet<string> _desired = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IMarketDataSource> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<IMarketDataSource, DateTimeOffset> _unhealthySince = new(ReferenceEqualityComparer.Instance);
    private readonly SemaphoreSlim _rebalanceGate = new(1, 1);

    private readonly ConcurrentDictionary<string, QuoteSnapshot> _quotes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, OrderBookState> _books = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Instrument> _instruments = new(StringComparer.Ordinal);
    private readonly TradeCandleAggregator _candles;

    private volatile bool _started;
    private volatile bool _suspended;
    private Timer? _timer;
    private int _maintenanceRunning;

    public MarketDataHub(IEnumerable<IMarketDataSource> sources, IClock clock, MarketDataHubOptions? options = null, ILogger<MarketDataHub>? logger = null)
    {
        _sources = sources.ToList();
        _clock = clock;
        _options = options ?? new MarketDataHubOptions();
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _candles = new TradeCandleAggregator(_options.AggregatedTimeframes, _options.OpenCandleUpdateInterval);
        foreach (var s in _sources)
        {
            var src = s;
            EventHandler<MarketEvent> h = (_, e) => OnSourceEvent(src, e);
            _handlers[s] = h;
            s.EventReceived += h;
        }
    }

    public event EventHandler<MarketEvent>? EventReceived;
    public IReadOnlyList<IMarketDataSource> Sources => _sources;
    public MarketDataHubOptions Options => _options;

    /// <summary>Aktuell zuständige Quelle für ein Symbol.</summary>
    public string? GetActiveSource(string symbol)
    {
        lock (_routeLock) return _active.TryGetValue(symbol, out var s) ? s.Name : null;
    }

    // ---------- Lebenszyklus ----------
    public async Task StartAsync(CancellationToken ct)
    {
        _started = true;
        _suspended = false;
        await ForEachSourceAsync(s => s.StartAsync(ct), "Start").ConfigureAwait(false);
        if (_options.MaintenanceInterval > TimeSpan.Zero && _timer is null)
            _timer = new Timer(_ => _ = RunMaintenanceAsync(), null, _options.MaintenanceInterval, _options.MaintenanceInterval);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _started = false;
        var t = Interlocked.Exchange(ref _timer, null);
        if (t is not null) await t.DisposeAsync().ConfigureAwait(false);
        await ForEachSourceAsync(s => s.StopAsync(ct), "Stopp").ConfigureAwait(false);
    }

    public async Task SuspendAsync(CancellationToken ct)
    {
        _suspended = true;
        await ForEachSourceAsync(s => s.SuspendAsync(ct), "Standby").ConfigureAwait(false);
    }

    public async Task ResumeAsync(CancellationToken ct)
    {
        try
        {
            await ForEachSourceAsync(s => s.ResumeAsync(ct), "Aufwachen").ConfigureAwait(false);
        }
        finally
        {
            _suspended = false;
            lock (_routeLock) _unhealthySince.Clear();
        }
        await RebalanceAsync(ct).ConfigureAwait(false);
    }

    private async Task ForEachSourceAsync(Func<IMarketDataSource, Task> action, string what)
    {
        var tasks = _sources.Select(async s =>
        {
            try { await action(s).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "{What} von {Source} fehlgeschlagen", what, s.Name); }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>Wartung: Kerzen per Zeitablauf schließen, Routing prüfen (Failover nach Karenz). Wird vom Timer aufgerufen.</summary>
    public async Task RunMaintenanceAsync()
    {
        if (Interlocked.Exchange(ref _maintenanceRunning, 1) == 1) return;
        try
        {
            var now = _clock.UtcNow;
            foreach (var c in _candles.CloseDue(now, _options.CandleCloseGrace))
                Raise(new CandleUpdate
                {
                    Symbol = c.Candle.Symbol, Source = c.Candle.Source, AsOfUtc = c.Candle.OpenTimeUtc + c.Candle.Timeframe.ToTimeSpan(),
                    ReceivedUtc = now, DelayedMinutes = c.DelayedMinutes, Quality = c.Quality, Candle = c.Candle,
                });
            if (_started && !_suspended) await RebalanceAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Wartung des MarketDataHub fehlgeschlagen"); }
        finally { Interlocked.Exchange(ref _maintenanceRunning, 0); }
    }

    // ---------- Routing ----------
    private static bool IsHealthy(ConnectionState s) => s is ConnectionState.Connected or ConnectionState.Connecting;

    private int Priority(IMarketDataSource s, string symbol)
    {
        var list = SymbolHelper.AssetClassOf(symbol) == AssetClass.Crypto ? _options.CryptoSourcePriority : _options.StockSourcePriority;
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i], s.Name, StringComparison.OrdinalIgnoreCase)) return i;
        return list.Count + IndexOf(s);
    }

    private int IndexOf(IMarketDataSource s)
    {
        for (int i = 0; i < _sources.Count; i++) if (ReferenceEquals(_sources[i], s)) return i;
        return int.MaxValue / 2;
    }

    private static bool SafeSupports(IMarketDataSource s, string symbol)
    {
        try { return s.Supports(symbol); } catch { return false; }
    }

    private List<IMarketDataSource> Candidates(string symbol)
        => _sources.Where(s => SafeSupports(s, symbol)).OrderBy(s => Priority(s, symbol)).ThenBy(IndexOf).ToList();

    private IMarketDataSource? SelectInitial(string symbol)
    {
        var c = Candidates(symbol);
        if (!_started) return c.FirstOrDefault();
        return c.FirstOrDefault(s => IsHealthy(s.State)) ?? c.FirstOrDefault();
    }

    public async Task SubscribeAsync(IEnumerable<string> symbols, CancellationToken ct)
    {
        var plan = new List<(IMarketDataSource Source, string Symbol)>();
        lock (_routeLock)
        {
            foreach (var s in symbols.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct())
            {
                _desired.Add(s);
                if (_active.ContainsKey(s)) continue;
                var src = SelectInitial(s);
                if (src is null) { _logger.LogWarning("Keine Datenquelle für {Symbol}", s); continue; }
                _active[s] = src;
                plan.Add((src, s));
            }
        }
        foreach (var g in plan.GroupBy(p => p.Source))
        {
            try { await g.Key.SubscribeAsync(g.Select(p => p.Symbol).ToList(), ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogWarning(ex, "Abo bei {Source} fehlgeschlagen", g.Key.Name); }
        }
    }

    public async Task UnsubscribeAsync(IEnumerable<string> symbols, CancellationToken ct)
    {
        var plan = new List<(IMarketDataSource Source, string Symbol)>();
        lock (_routeLock)
        {
            foreach (var s in symbols.Distinct())
            {
                _desired.Remove(s);
                if (_active.Remove(s, out var src)) plan.Add((src, s));
            }
        }
        foreach (var g in plan.GroupBy(p => p.Source))
        {
            try { await g.Key.UnsubscribeAsync(g.Select(p => p.Symbol).ToList(), ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogWarning(ex, "Abo-Ende bei {Source} fehlgeschlagen", g.Key.Name); }
        }
        foreach (var (_, s) in plan) { _books.TryRemove(s, out _); _candles.Reset(s); }
    }

    /// <summary>
    /// Prüft für jedes abonnierte Symbol, ob die beste gesunde Quelle zuständig ist: Failover, wenn die aktive Quelle
    /// länger als die Karenz nicht verbunden ist (Failed/Disconnected sofort); Failback, sobald eine bevorzugte Quelle wieder verbunden ist.
    /// </summary>
    public async Task RebalanceAsync(CancellationToken ct)
    {
        await _rebalanceGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _clock.UtcNow;
            var switches = new List<(string Symbol, IMarketDataSource? From, IMarketDataSource To)>();
            lock (_routeLock)
            {
                foreach (var sym in _desired)
                {
                    _active.TryGetValue(sym, out var cur);
                    var candidates = Candidates(sym);
                    var best = candidates.FirstOrDefault(c => IsHealthy(c.State));
                    if (best is null || ReferenceEquals(best, cur)) continue;
                    if (cur is not null && !IsHealthy(cur.State))
                    {
                        bool hardDown = cur.State is ConnectionState.Failed or ConnectionState.Disconnected;
                        var since = _unhealthySince.TryGetValue(cur, out var t) ? t : now;
                        if (!hardDown && now - since < _options.FailoverGracePeriod) continue;
                    }
                    else if (cur is not null && Priority(best, sym) >= Priority(cur, sym))
                        continue; // aktuelle Quelle gesund und mindestens gleich gut
                    _active[sym] = best;
                    switches.Add((sym, cur, best));
                }
            }
            foreach (var (sym, from, to) in switches)
            {
                _books.TryRemove(sym, out _);
                if (from is not null)
                {
                    try { await from.UnsubscribeAsync([sym], ct).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogDebug(ex, "Abo-Ende bei {Source}", from.Name); }
                }
                try { await to.SubscribeAsync([sym], ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogWarning(ex, "Abo bei {Source} fehlgeschlagen", to.Name); }
                var msg = from is null ? $"{sym}: Datenquelle {to.Name}" : $"{sym}: Datenquelle gewechselt {from.Name} → {to.Name}";
                _logger.LogInformation("{Message}", msg);
                Raise(new ConnectionStatusChanged
                {
                    Symbol = sym, Source = to.Name, AsOfUtc = now, ReceivedUtc = now, DelayedMinutes = to.DelayedMinutes,
                    Quality = to.IsRealtime ? DataQuality.Realtime : DataQuality.Delayed, State = to.State, Message = msg,
                });
            }
        }
        finally { _rebalanceGate.Release(); }
    }

    // ---------- Ereignisse ----------
    private void Raise(MarketEvent e)
    {
        var h = EventReceived;
        if (h is null) return;
        try { h(this, e); }
        catch (Exception ex) { _logger.LogError(ex, "Fehler in einem Abonnenten des MarketDataHub"); }
    }

    private void OnSourceEvent(IMarketDataSource source, MarketEvent e)
    {
        if (e is ConnectionStatusChanged st)
        {
            Raise(st);
            if (st.Symbol == "*") OnSourceStateChanged(source, st.State);
            return;
        }
        lock (_routeLock)
        {
            // Nur die zuständige Quelle zählt (nach Failover liefern ggf. kurz zwei Quellen).
            if (_active.TryGetValue(e.Symbol, out var act) && !ReferenceEquals(act, source)) return;
        }
        var now = _clock.UtcNow;
        switch (e)
        {
            case QuoteTick q:
                UpdateQuote(q);
                Raise(q);
                break;
            case TradeTick t:
                UpdateQuoteFromTrade(t);
                Raise(t);
                foreach (var c in _candles.AddTrade(t.Symbol, t.Source, t.AsOfUtc, t.Price, t.Quantity, now, t.Quality, t.DelayedMinutes))
                    Raise(new CandleUpdate
                    {
                        Symbol = t.Symbol, Source = t.Source,
                        AsOfUtc = c.Candle.IsClosed ? c.Candle.OpenTimeUtc + c.Candle.Timeframe.ToTimeSpan() : t.AsOfUtc,
                        ReceivedUtc = now, DelayedMinutes = c.DelayedMinutes, Quality = c.Quality, Candle = c.Candle,
                    });
                break;
            case OrderBookUpdate b:
                var book = _books.GetOrAdd(b.Symbol, _ => new OrderBookState(_options.MaxOrderBookDepth));
                if (b.IsSnapshot) book.Apply(b);
                else if (book.HasSnapshot && book.Source == b.Source) book.Apply(b);
                Raise(b);
                break;
            case CandleUpdate cu:
                if (_options.ForwardSourceCandles || cu.Candle.IsBackfill) Raise(cu);
                break;
            default:
                Raise(e);
                break;
        }
    }

    private void OnSourceStateChanged(IMarketDataSource source, ConnectionState state)
    {
        lock (_routeLock)
        {
            if (IsHealthy(state)) _unhealthySince.Remove(source);
            else if (!_unhealthySince.ContainsKey(source)) _unhealthySince[source] = _clock.UtcNow;
        }
        if (!_started || _suspended) return;
        _ = Task.Run(async () =>
        {
            try { await RebalanceAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Routing-Prüfung fehlgeschlagen"); }
        });
    }

    private string CurrencyOf(string symbol)
        => _instruments.TryGetValue(symbol, out var i) ? i.QuoteCurrency : SymbolHelper.QuoteCurrencyOf(symbol);

    private void UpdateQuote(QuoteTick q)
    {
        _quotes.AddOrUpdate(q.Symbol, _ => Build(null), (_, prev) => Build(prev));

        QuoteSnapshot Build(QuoteSnapshot? prev)
        {
            var same = prev is not null && prev.Source == q.Source;
            return new QuoteSnapshot
            {
                Symbol = q.Symbol,
                Source = q.Source,
                AsOfUtc = same && prev!.AsOfUtc > q.AsOfUtc ? prev.AsOfUtc : q.AsOfUtc,
                DelayedMinutes = q.DelayedMinutes,
                IsRealtime = q.IsRealtime,
                Quality = q.Quality,
                Bid = q.Bid ?? (same ? prev!.Bid : null),
                Ask = q.Ask ?? (same ? prev!.Ask : null),
                Last = q.Last ?? (same ? prev!.Last : null),
                ChangePercent = q.ChangePercent ?? (same ? prev!.ChangePercent : null),
                Volume24h = q.Volume24h ?? (same ? prev!.Volume24h : null),
                Currency = CurrencyOf(q.Symbol),
            };
        }
    }

    private void UpdateQuoteFromTrade(TradeTick t)
    {
        _quotes.AddOrUpdate(t.Symbol, _ => Build(null), (_, prev) => Build(prev));

        QuoteSnapshot Build(QuoteSnapshot? prev)
        {
            var same = prev is not null && prev.Source == t.Source;
            if (same && prev!.AsOfUtc > t.AsOfUtc) return prev; // älterer Trade
            return new QuoteSnapshot
            {
                Symbol = t.Symbol,
                Source = t.Source,
                AsOfUtc = t.AsOfUtc,
                DelayedMinutes = t.DelayedMinutes,
                IsRealtime = t.IsRealtime,
                Quality = t.Quality,
                Bid = same ? prev!.Bid : null,
                Ask = same ? prev!.Ask : null,
                Last = t.Price,
                ChangePercent = same ? prev!.ChangePercent : null,
                Volume24h = same ? prev!.Volume24h : null,
                Currency = CurrencyOf(t.Symbol),
            };
        }
    }

    // ---------- Abfragen ----------
    public QuoteSnapshot? GetLatestQuote(string symbol) => _quotes.TryGetValue(symbol, out var q) ? q : null;

    public IReadOnlyDictionary<string, QuoteSnapshot> GetAllQuotes() => new Dictionary<string, QuoteSnapshot>(_quotes, StringComparer.Ordinal);

    public OrderBookSnapshot? GetLatestOrderBook(string symbol)
        => _books.TryGetValue(symbol, out var b) && b.HasSnapshot ? b.ToSnapshot(symbol) : null;

    /// <summary>Laufende (offene) Kerze aus der Trade-Aggregation.</summary>
    public Candle? GetCurrentCandle(string symbol, Timeframe timeframe) => _candles.GetCurrent(symbol, timeframe);

    public IReadOnlyDictionary<string, ConnectionState> GetConnectionStates()
    {
        var d = new Dictionary<string, ConnectionState>(StringComparer.Ordinal);
        foreach (var s in _sources) d[s.Name] = s.State;
        return d;
    }

    public Instrument? GetInstrument(string symbol)
    {
        if (_instruments.TryGetValue(symbol, out var i)) return i;
        var src = GetActiveSource(symbol);
        if (SymbolHelper.IsCryptoPair(symbol)) return SymbolHelper.SynthesizeCryptoInstrument(symbol, src);
        if (SymbolHelper.LooksLikeIsin(symbol)) return GettexInstruments.ForIsin(symbol, src ?? "gettex");
        return null;
    }

    public async Task<IReadOnlyList<Instrument>> SearchSymbolsAsync(string query, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_options.SearchTimeout);
        var tasks = _sources.Select(async s =>
        {
            try { return (Source: s, Items: await s.SearchSymbolsAsync(query, cts.Token).ConfigureAwait(false)); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "Suche bei {Source} fehlgeschlagen", s.Name);
                return (Source: s, Items: (IReadOnlyList<Instrument>)[]);
            }
        }).ToList();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        var merged = new Dictionary<string, (Instrument Inst, int Prio, int Rank)>(StringComparer.Ordinal);
        foreach (var (src, items) in results)
        {
            for (int rank = 0; rank < items.Count; rank++)
            {
                var inst = items[rank];
                var prio = Priority(src, inst.Symbol);
                if (!merged.TryGetValue(inst.Symbol, out var existing) || prio < existing.Prio)
                    merged[inst.Symbol] = (inst, prio, Math.Min(rank, existing.Inst is null ? rank : existing.Rank));
            }
        }
        foreach (var (sym, v) in merged) _instruments[sym] = v.Inst;
        var q = query.Trim();
        return merged.Values
            .OrderByDescending(v => v.Inst.Symbol.Equals(q.Replace('-', '/'), StringComparison.OrdinalIgnoreCase) || (v.Inst.Isin?.Equals(q, StringComparison.OrdinalIgnoreCase) ?? false))
            .ThenBy(v => v.Rank)
            .ThenBy(v => v.Prio)
            .ThenBy(v => v.Inst.Symbol, StringComparer.Ordinal)
            .Select(v => v.Inst)
            .Take(50)
            .ToList();
    }

    /// <summary>Historie: zuerst die bevorzugte Quelle, sofern ihr Rückblick reicht (Kraken ~720 Kerzen), sonst Bitvavo/Coinbase.</summary>
    public async Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, Timeframe timeframe, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var routing = Candidates(symbol);
        bool Fits(IMarketDataSource s) => s is not IHistoryCapabilities h || h.GetMaxHistoryLookback(timeframe) is not { } lb || fromUtc is null || fromUtc.Value >= now - lb;
        int LongPrio(IMarketDataSource s)
        {
            for (int i = 0; i < _options.LongHistorySourcePriority.Count; i++)
                if (string.Equals(_options.LongHistorySourcePriority[i], s.Name, StringComparison.OrdinalIgnoreCase)) return i;
            return _options.LongHistorySourcePriority.Count + Priority(s, symbol);
        }
        IEnumerable<IMarketDataSource> order = routing.Count > 0 && Fits(routing[0])
            ? routing.Where(Fits).Concat(routing.Where(s => !Fits(s)))
            : routing.Where(Fits).OrderBy(LongPrio).Concat(routing.Where(s => !Fits(s)));

        Exception? last = null;
        foreach (var s in order)
        {
            try
            {
                var candles = await s.GetCandlesAsync(symbol, timeframe, fromUtc, toUtc, ct).ConfigureAwait(false);
                if (candles.Count > 0) return candles;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                last = ex;
                _logger.LogDebug(ex, "Kerzen von {Source} für {Symbol} nicht ladbar", s.Name, symbol);
            }
        }
        if (last is not null) _logger.LogWarning("Keine Kerzen für {Symbol}: {Error}", symbol, last.Message);
        return [];
    }

    public async Task<OrderBookSnapshot?> GetOrderBookAsync(string symbol, int depth, CancellationToken ct)
    {
        if (_books.TryGetValue(symbol, out var b) && b.HasSnapshot) return b.ToSnapshot(symbol, depth: depth);
        IMarketDataSource? src;
        lock (_routeLock) _active.TryGetValue(symbol, out src);
        foreach (var s in src is not null ? [src] : Candidates(symbol))
        {
            try
            {
                var ob = await s.GetOrderBookAsync(symbol, depth, ct).ConfigureAwait(false);
                if (ob is not null) return ob;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogDebug(ex, "Orderbuch von {Source} nicht ladbar", s.Name); }
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        var t = Interlocked.Exchange(ref _timer, null);
        if (t is not null) await t.DisposeAsync().ConfigureAwait(false);
        foreach (var (s, h) in _handlers) s.EventReceived -= h;
        _handlers.Clear();
    }
}
