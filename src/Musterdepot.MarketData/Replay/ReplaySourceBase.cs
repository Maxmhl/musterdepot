using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;
using Musterdepot.MarketData.Gettex;
using Musterdepot.MarketData.Infrastructure;
using Musterdepot.MarketData.Sources;

namespace Musterdepot.MarketData.Replay;

public record ReplayOptions
{
    /// <summary>Geschwindigkeitsfaktor (1 = Echtzeit, 60 = eine Minute je Sekunde).</summary>
    public double Speed { get; init; } = 1.0;
    /// <summary>Takt der Abspielschleife (Echtzeit).</summary>
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromMilliseconds(100);
    /// <summary>Nach StartAsync sofort abspielen (sonst nur über AdvanceTo/Step/Play).</summary>
    public bool AutoPlay { get; init; } = true;
    /// <summary>Ruhepausen länger als dieser Wert werden übersprungen (z. B. Nacht), null = nie springen.</summary>
    public TimeSpan? MaxIdleGap { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>Uhr beim Laden auf den Zeitpunkt des ersten Ereignisses setzen.</summary>
    public bool StartAtFirstEvent { get; init; } = true;
}

/// <summary>
/// Spielt eine vorab geladene, zeitlich sortierte Ereignisliste mit simulierter Uhr (<see cref="ManualClock"/>) ab:
/// Geschwindigkeitsfaktor, Pause, schrittweise oder bis zu einem Zeitpunkt. Kerzen und Orderbücher werden nur aus
/// bereits abgespielten Ereignissen gebildet (kein Blick in die Zukunft).
/// </summary>
public abstract class ReplaySourceBase : MarketDataSourceBase
{
    private readonly ManualClock _clock;
    private readonly ReplayOptions _options;
    private readonly Lock _emitLock = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private IReadOnlyList<(DateTimeOffset EmitAt, MarketEvent Event)> _events = [];
    private bool _loaded;
    private int _position;
    private double _speed;
    private volatile bool _paused;
    private CancellationTokenSource? _runCts;
    private Task? _runTask;

    private readonly Dictionary<string, List<(DateTimeOffset Time, decimal Price, decimal Qty)>> _playedTrades = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Symbol, Timeframe Tf), SortedDictionary<DateTimeOffset, Candle>> _playedCandles = new();
    private readonly Dictionary<string, OrderBookState> _books = new(StringComparer.Ordinal);
    private readonly HashSet<string> _symbols = new(StringComparer.Ordinal);

    protected ReplaySourceBase(string name, ManualClock clock, ReplayOptions options, ILogger? logger)
        : base(name, clock, logger)
    {
        _clock = clock;
        _options = options;
        _speed = options.Speed > 0 ? options.Speed : 1.0;
        _paused = !options.AutoPlay;
    }

    public ManualClock ReplayClock => _clock;
    public double Speed { get => _speed; set => _speed = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value)); }
    public bool IsPaused => _paused;
    public bool IsLoaded => _loaded;
    public int Position { get { lock (_emitLock) return _position; } }
    public int Count => _events.Count;
    public bool IsFinished => _loaded && Position >= Count;
    public DateTimeOffset? NextEventTime { get { lock (_emitLock) return _position < _events.Count ? _events[_position].EmitAt : null; } }
    public Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } = Task.Delay;

    public override IReadOnlyCollection<AssetClass> AssetClasses { get; } = [AssetClass.Crypto, AssetClass.Stock, AssetClass.Etf];
    protected override DataQuality DefaultQuality => DataQuality.Replay;
    public override bool IsRealtime => false;

    /// <summary>Lädt alle Ereignisse (wird einmalig aufgerufen).</summary>
    protected abstract Task<IReadOnlyList<MarketEvent>> LoadEventsAsync(CancellationToken ct);
    /// <summary>Zeitpunkt der simulierten Uhr, zu dem das Ereignis erscheint.</summary>
    protected virtual DateTimeOffset EmitTime(MarketEvent e) => e.AsOfUtc;
    /// <summary>Ereignis beim Abspielen anpassen (Empfangszeit, Qualität, Quelle).</summary>
    protected virtual MarketEvent Restamp(MarketEvent e, DateTimeOffset nowUtc) => e with { ReceivedUtc = nowUtc };

    public async Task LoadAsync(CancellationToken ct)
    {
        await _loadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_loaded) return;
            var events = await LoadEventsAsync(ct).ConfigureAwait(false);
            // Stabile Sortierung: gleiche Zeitstempel behalten die Dateireihenfolge.
            var sorted = events.Select(e => (EmitAt: EmitTime(e), Event: e)).OrderBy(x => x.EmitAt).ToList();
            lock (_emitLock)
            {
                _events = sorted;
                _position = 0;
                foreach (var (_, e) in sorted) if (e.Symbol != "*") _symbols.Add(e.Symbol);
                _loaded = true;
            }
            if (_options.StartAtFirstEvent && sorted.Count > 0) _clock.Set(sorted[0].EmitAt);
        }
        finally { _loadGate.Release(); }
    }

    // ---------- Lebenszyklus ----------
    public override async Task StartAsync(CancellationToken ct)
    {
        await LoadAsync(ct).ConfigureAwait(false);
        SetState(ConnectionState.Connected, $"Replay bereit: {Count} Ereignisse ab {NextEventTime:yyyy-MM-dd HH:mm:ss} UTC");
        lock (_emitLock)
        {
            if (_runTask is { IsCompleted: false }) return;
            _runCts = new CancellationTokenSource();
            var token = _runCts.Token;
            _runTask = Task.Run(() => RunAsync(token), CancellationToken.None);
        }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        Task? task;
        CancellationTokenSource? cts;
        lock (_emitLock) { task = _runTask; cts = _runCts; _runTask = null; _runCts = null; }
        if (cts is not null)
        {
            cts.Cancel();
            if (task is not null) { try { await task.ConfigureAwait(false); } catch { /* beendet */ } }
            cts.Dispose();
        }
        SetState(ConnectionState.Disconnected, "Replay gestoppt");
    }

    public void Pause() => _paused = true;
    public void Play() => _paused = false;

    public override Task SuspendAsync(CancellationToken ct)
    {
        Pause();
        SetState(ConnectionState.Suspended, "Replay pausiert (Standby)");
        return Task.CompletedTask;
    }

    public override Task ResumeAsync(CancellationToken ct)
    {
        SetState(IsFinished ? ConnectionState.Disconnected : ConnectionState.Connected);
        Play();
        return Task.CompletedTask;
    }

    protected override Task DisconnectForSuspendAsync(CancellationToken ct) { Pause(); return Task.CompletedTask; }
    protected override Task ReconnectAfterSuspendAsync(CancellationToken ct) { Play(); return Task.CompletedTask; }

    private async Task RunAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var lastReal = sw.Elapsed;
        while (!ct.IsCancellationRequested && !IsFinished)
        {
            try { await DelayAsync(_options.TickInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            var real = sw.Elapsed;
            var elapsed = real - lastReal;
            lastReal = real;
            if (_paused) continue;
            var target = _clock.UtcNow + TimeSpan.FromTicks((long)(elapsed.Ticks * _speed));
            if (_options.MaxIdleGap is { } gap && NextEventTime is { } next && next - _clock.UtcNow > gap && next > target)
                target = next;
            AdvanceTo(target);
        }
    }

    // ---------- Abspielen ----------

    /// <summary>Spielt alle Ereignisse bis einschließlich <paramref name="targetUtc"/> ab und stellt die Uhr dorthin. Rückgabe: Anzahl.</summary>
    public int AdvanceTo(DateTimeOffset targetUtc)
    {
        int n = 0;
        lock (_emitLock)
        {
            if (!_loaded) throw new InvalidOperationException("Replay ist noch nicht geladen (LoadAsync/StartAsync).");
            while (_position < _events.Count && _events[_position].EmitAt <= targetUtc)
            {
                var (at, e) = _events[_position++];
                if (_clock.UtcNow < at) _clock.Set(at);
                Emit(e, at);
                n++;
            }
            if (_clock.UtcNow < targetUtc) _clock.Set(targetUtc);
        }
        if (n > 0 && IsFinished) SetState(ConnectionState.Disconnected, "Replay beendet");
        return n;
    }

    public int Advance(TimeSpan simulated) => AdvanceTo(_clock.UtcNow + simulated);

    /// <summary>Spielt alle Ereignisse des nächsten Zeitstempels ab.</summary>
    public int Step()
    {
        var next = NextEventTime;
        return next is null ? 0 : AdvanceTo(next.Value);
    }

    /// <summary>Spielt alles bis zum Ende ab (Backtest im Schnelldurchlauf).</summary>
    public int RunToEnd() => _events.Count == 0 ? 0 : AdvanceTo(_events[^1].EmitAt);

    private void Emit(MarketEvent original, DateTimeOffset at)
    {
        var subs = Subscriptions;
        if (original.Symbol != "*" && subs.Count > 0 && !subs.Contains(original.Symbol)) return;
        var e = Restamp(original, at);
        switch (e)
        {
            case TradeTick t:
                if (!_playedTrades.TryGetValue(t.Symbol, out var list)) _playedTrades[t.Symbol] = list = [];
                list.Add((t.AsOfUtc, t.Price, t.Quantity));
                break;
            case CandleUpdate { Candle.IsClosed: true } cu:
                var key = (cu.Symbol, cu.Candle.Timeframe);
                if (!_playedCandles.TryGetValue(key, out var dict)) _playedCandles[key] = dict = [];
                dict[cu.Candle.OpenTimeUtc] = cu.Candle;
                break;
            case GapDetected g:
                foreach (var c in g.BackfilledCandles)
                {
                    var k = (g.Symbol, c.Timeframe);
                    if (!_playedCandles.TryGetValue(k, out var d)) _playedCandles[k] = d = [];
                    d[c.OpenTimeUtc] = c;
                }
                break;
            case OrderBookUpdate b:
                if (!_books.TryGetValue(b.Symbol, out var book)) _books[b.Symbol] = book = new OrderBookState();
                book.Apply(b);
                break;
        }
        Publish(e);
    }

    // ---------- Abfragen ----------
    public override bool Supports(string symbol)
    {
        lock (_emitLock) return _loaded ? _symbols.Contains(symbol) : SymbolHelper.AssetClassOf(symbol) is not null;
    }

    public override Task<IReadOnlyList<Instrument>> SearchSymbolsAsync(string query, CancellationToken ct)
    {
        List<string> symbols;
        lock (_emitLock) symbols = _symbols.ToList();
        var all = symbols.Select(s => SymbolHelper.IsCryptoPair(s)
            ? SymbolHelper.SynthesizeCryptoInstrument(s, Name)
            : GettexInstruments.ForIsin(s, Name));
        return Task.FromResult(FilterInstruments(all, query));
    }

    public override Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, Timeframe timeframe, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var to = toUtc is { } t && t < now ? t : now;
        var from = fromUtc is { } f ? timeframe.Floor(f) : DateTimeOffset.MinValue;
        IReadOnlyList<Candle> result;
        lock (_emitLock)
        {
            if (_playedCandles.TryGetValue((symbol, timeframe), out var exact) && exact.Count > 0)
                result = exact.Values.ToList();
            else if (_playedCandles.TryGetValue((symbol, Timeframe.M1), out var m1) && m1.Count > 0)
                result = TradeCandleAggregator.Resample(m1.Values, timeframe);
            else if (_playedTrades.TryGetValue(symbol, out var trades))
                result = TradeCandleAggregator.FromTrades(symbol, Name, timeframe, trades.ToList(), now);
            else
                result = [];
        }
        return Task.FromResult<IReadOnlyList<Candle>>(result.Where(c => c.OpenTimeUtc >= from && c.OpenTimeUtc < to).ToList());
    }

    public override Task<OrderBookSnapshot?> GetOrderBookAsync(string symbol, int depth, CancellationToken ct)
    {
        lock (_emitLock)
            return Task.FromResult(_books.TryGetValue(symbol, out var b) && b.HasSnapshot ? b.ToSnapshot(symbol, depth: depth) : null);
    }
}
