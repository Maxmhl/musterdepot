using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;

namespace Musterdepot.MarketData.Sources;

/// <summary>Optionale Fähigkeit: wie weit eine Quelle Kerzen zurück liefern kann (null = unbegrenzt).</summary>
public interface IHistoryCapabilities
{
    TimeSpan? GetMaxHistoryLookback(Timeframe timeframe);
}

/// <summary>
/// Gemeinsame Basis aller Quellen: Ereignisse, Verbindungsstatus, Abo-Liste, Standby mit Lücken-Nachladen.
/// </summary>
public abstract class MarketDataSourceBase : IMarketDataSource
{
    private readonly HashSet<string> _subscriptions = new(StringComparer.Ordinal);
    private readonly Lock _subLock = new();
    private volatile ConnectionState _state = ConnectionState.Disconnected;
    private readonly Lock _stateLock = new();

    protected MarketDataSourceBase(string name, IClock clock, ILogger? logger)
    {
        Name = name;
        Clock = clock;
        Logger = logger ?? NullLogger.Instance;
    }

    public string Name { get; }
    protected IClock Clock { get; }
    protected ILogger Logger { get; }

    public abstract IReadOnlyCollection<AssetClass> AssetClasses { get; }
    public virtual bool IsRealtime => DelayedMinutes == 0;
    public virtual int DelayedMinutes => 0;
    /// <summary>Qualitätsflag der normalen Ereignisse dieser Quelle.</summary>
    protected virtual DataQuality DefaultQuality => DelayedMinutes > 0 ? DataQuality.Delayed : DataQuality.Realtime;
    public ConnectionState State => _state;
    /// <summary>Letzte Statusmeldung (z. B. Fehlertext, Formatwechsel).</summary>
    public string? LastStatusMessage { get; private set; }
    /// <summary>Zeitpunkt des letzten Standbys (für das Nachladen).</summary>
    public DateTimeOffset? SuspendedAtUtc { get; protected set; }

    public event EventHandler<MarketEvent>? EventReceived;

    public IReadOnlyCollection<string> Subscriptions
    {
        get { lock (_subLock) return _subscriptions.ToArray(); }
    }

    protected bool IsSubscribed(string symbol)
    {
        lock (_subLock) return _subscriptions.Contains(symbol);
    }

    /// <summary>Meldet ein Ereignis an alle Abonnenten; Fehler der Abonnenten werden protokolliert, nie weitergeworfen.</summary>
    protected void Publish(MarketEvent e)
    {
        var handler = EventReceived;
        if (handler is null) return;
        try { handler(this, e); }
        catch (Exception ex) { Logger.LogError(ex, "Fehler in einem Abonnenten von {Source}", Name); }
    }

    protected void PublishAll(IEnumerable<MarketEvent> events)
    {
        foreach (var e in events) Publish(e);
    }

    /// <summary>Setzt den Verbindungsstatus und meldet <see cref="ConnectionStatusChanged"/> (nur bei Änderung oder neuer Meldung).</summary>
    protected void SetState(ConnectionState state, string? message = null, bool force = false)
    {
        lock (_stateLock)
        {
            if (!force && _state == state && message == LastStatusMessage) return;
            _state = state;
            LastStatusMessage = message;
        }
        if (message is not null) Logger.LogInformation("{Source}: {State} – {Message}", Name, state, message);
        var now = Clock.UtcNow;
        Publish(new ConnectionStatusChanged
        {
            Symbol = "*",
            Source = Name,
            AsOfUtc = now,
            ReceivedUtc = now,
            DelayedMinutes = DelayedMinutes,
            Quality = DefaultQuality,
            State = state,
            Message = message,
        });
    }

    /// <summary>Warnung/Hinweis bei unverändertem Status (z. B. Formatwechsel, Prüfsummenfehler).</summary>
    protected void PublishWarning(string message) => SetState(_state, message, force: true);

    // ---------- Lebenszyklus ----------
    public abstract Task StartAsync(CancellationToken ct);
    public abstract Task StopAsync(CancellationToken ct);

    /// <summary>Verbindung für Standby trennen (ohne Status zu setzen).</summary>
    protected abstract Task DisconnectForSuspendAsync(CancellationToken ct);
    /// <summary>Nach Standby wieder verbinden.</summary>
    protected abstract Task ReconnectAfterSuspendAsync(CancellationToken ct);

    public virtual async Task SuspendAsync(CancellationToken ct)
    {
        if (_state == ConnectionState.Suspended) return;
        SuspendedAtUtc = Clock.UtcNow;
        await DisconnectForSuspendAsync(ct).ConfigureAwait(false);
        SetState(ConnectionState.Suspended, "Standby");
    }

    public virtual async Task ResumeAsync(CancellationToken ct)
    {
        var since = SuspendedAtUtc;
        SuspendedAtUtc = null;
        await ReconnectAfterSuspendAsync(ct).ConfigureAwait(false);
        if (since is { } s) await BackfillGapAsync(s, Clock.UtcNow, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Lädt für alle abonnierten Symbole die abgeschlossenen 1-Min-Kerzen der Lücke nach und meldet je Symbol
    /// ein <see cref="GapDetected"/> (Kerzen zeitlich sortiert, IsBackfill = true).
    /// </summary>
    public async Task BackfillGapAsync(DateTimeOffset gapStartUtc, DateTimeOffset gapEndUtc, CancellationToken ct)
    {
        if (gapEndUtc <= gapStartUtc) return;
        var from = Timeframe.M1.Floor(gapStartUtc);
        foreach (var symbol in Subscriptions.OrderBy(s => s, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<Candle> candles;
            try
            {
                candles = await GetCandlesAsync(symbol, Timeframe.M1, from, gapEndUtc, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "{Source}: Nachladen für {Symbol} fehlgeschlagen", Name, symbol);
                PublishWarning($"Nachladen der Lücke für {symbol} fehlgeschlagen: {ex.Message}");
                candles = [];
            }
            var backfill = candles
                .Where(c => c.OpenTimeUtc >= from && c.OpenTimeUtc + TimeSpan.FromMinutes(1) <= gapEndUtc)
                .GroupBy(c => c.OpenTimeUtc).Select(g => g.Last())
                .OrderBy(c => c.OpenTimeUtc)
                .Select(c => c with { IsBackfill = true, IsClosed = true, Source = string.IsNullOrEmpty(c.Source) ? Name : c.Source })
                .ToList();
            var now = Clock.UtcNow;
            Publish(new GapDetected
            {
                Symbol = symbol,
                Source = Name,
                AsOfUtc = gapEndUtc,
                ReceivedUtc = now,
                DelayedMinutes = DelayedMinutes,
                Quality = DataQuality.Backfill,
                GapStartUtc = gapStartUtc,
                GapEndUtc = gapEndUtc,
                BackfilledCandles = backfill,
            });
        }
    }

    // ---------- Abos ----------
    public abstract bool Supports(string symbol);

    public async Task SubscribeAsync(IEnumerable<string> symbols, CancellationToken ct)
    {
        List<string> added = [];
        lock (_subLock)
            foreach (var s in symbols)
                if (Supports(s) && _subscriptions.Add(s)) added.Add(s);
        if (added.Count > 0) await OnSubscribedAsync(added, ct).ConfigureAwait(false);
    }

    public async Task UnsubscribeAsync(IEnumerable<string> symbols, CancellationToken ct)
    {
        List<string> removed = [];
        lock (_subLock)
            foreach (var s in symbols)
                if (_subscriptions.Remove(s)) removed.Add(s);
        if (removed.Count > 0) await OnUnsubscribedAsync(removed, ct).ConfigureAwait(false);
    }

    protected virtual Task OnSubscribedAsync(IReadOnlyList<string> symbols, CancellationToken ct) => Task.CompletedTask;
    protected virtual Task OnUnsubscribedAsync(IReadOnlyList<string> symbols, CancellationToken ct) => Task.CompletedTask;

    public abstract Task<IReadOnlyList<Instrument>> SearchSymbolsAsync(string query, CancellationToken ct);
    public abstract Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, Timeframe timeframe, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, CancellationToken ct);
    public abstract Task<OrderBookSnapshot?> GetOrderBookAsync(string symbol, int depth, CancellationToken ct);

    public virtual async ValueTask DisposeAsync()
    {
        try { await StopAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { Logger.LogDebug(ex, "{Source}: Fehler beim Beenden", Name); }
        GC.SuppressFinalize(this);
    }

    /// <summary>Filtert Instrumente nach Suchbegriff (Symbol, Name, ISIN, WKN, Basis-Asset).</summary>
    protected static IReadOnlyList<Instrument> FilterInstruments(IEnumerable<Instrument> all, string query, int max = 50)
    {
        var q = (query ?? "").Trim();
        if (q.Length == 0) return all.Take(max).ToList();
        var qn = q.Replace("-", "/").Replace(" ", "");
        return all
            .Select(i => (i, score: Score(i, q, qn)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score).ThenBy(x => x.i.Symbol, StringComparer.Ordinal)
            .Take(max)
            .Select(x => x.i)
            .ToList();

        static int Score(Instrument i, string q, string qn)
        {
            if (i.Symbol.Equals(qn, StringComparison.OrdinalIgnoreCase) || (i.Isin?.Equals(q, StringComparison.OrdinalIgnoreCase) ?? false)) return 100;
            if (i.BaseAsset?.Equals(q, StringComparison.OrdinalIgnoreCase) ?? false) return 80;
            if (i.Wkn?.Equals(q, StringComparison.OrdinalIgnoreCase) ?? false) return 90;
            if (i.Symbol.StartsWith(qn, StringComparison.OrdinalIgnoreCase)) return 60;
            if (i.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 50;
            if (i.Symbol.Contains(qn, StringComparison.OrdinalIgnoreCase)) return 30;
            if (i.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) return 20;
            return 0;
        }
    }
}
