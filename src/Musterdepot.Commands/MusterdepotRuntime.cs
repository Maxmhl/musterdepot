using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;
using Musterdepot.Core.Events;
using Musterdepot.Core.MarketData;

namespace Musterdepot.Commands;

/// <summary>
/// Verbindet Marktdaten → Engine → Persistence und treibt die Uhr.
/// Start: Einstellungen laden, Events in die Engine laden, Standardkonto anlegen (falls nötig),
/// Engine-Events geordnet (Channel, ein Schreiber) in den <see cref="IEventStore"/> schreiben,
/// Marktdaten an die Engine, abgeschlossene Kerzen in den <see cref="ICandleStore"/> (Prune einmal täglich),
/// Uhr-Takt an die Engine, Symbole aus Watchlists, Positionen und offenen Orders abonnieren, Hub starten.
/// </summary>
public sealed class MusterdepotRuntime : IMusterdepotRuntime, IDisposable
{
    private readonly ITradingEngine _engine;
    private readonly IMarketDataHub _hub;
    private readonly IEventStore _eventStore;
    private readonly ICandleStore _candleStore;
    private readonly IWatchlistStore _watchlists;
    private readonly SettingsService _settings;
    private readonly ReadModels _readModels;
    private readonly AlertMonitor _alertMonitor;
    private readonly StoreChangeNotifier _changes;
    private readonly IClock _clock;
    private readonly CommandOptions _options;
    private readonly ILogger<MusterdepotRuntime> _logger;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly HashSet<string> _subscribed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _subscribeLock = new();

    private CancellationTokenSource? _cts;
    private Channel<IReadOnlyList<DomainEvent>>? _eventChannel;
    private Channel<IReadOnlyList<Candle>>? _candleChannel;
    private Task? _persistTask;
    private Task? _candleTask;
    private Task? _tickTask;
    private volatile bool _running;
    private volatile bool _suspended;
    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;
    private int _pruning;

    public MusterdepotRuntime(
        ITradingEngine engine,
        IMarketDataHub hub,
        IEventStore eventStore,
        ICandleStore candleStore,
        IWatchlistStore watchlists,
        SettingsService settings,
        ReadModels readModels,
        AlertMonitor alertMonitor,
        CommandBus commandBus, // sorgt dafür, dass die Tagesverlust-/Not-Aus-Überwachung des Busses aktiv ist
        StoreChangeNotifier changes,
        IClock clock,
        CommandOptions options,
        ILogger<MusterdepotRuntime> logger)
    {
        _engine = engine;
        _hub = hub;
        _eventStore = eventStore;
        _candleStore = candleStore;
        _watchlists = watchlists;
        _settings = settings;
        _readModels = readModels;
        _alertMonitor = alertMonitor;
        _changes = changes;
        _clock = clock;
        _options = options;
        _logger = logger;
        GC.KeepAlive(commandBus);
    }

    public bool IsRunning => _running;
    public bool IsSuspended => _suspended;

    /// <summary>Symbole, die die Laufzeit abonniert hat.</summary>
    public IReadOnlyCollection<string> SubscribedSymbols { get { lock (_subscribeLock) return _subscribed.ToList(); } }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_running) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            var settings = await _settings.LoadAsync(ct).ConfigureAwait(false);

            var events = await _eventStore.ReadAllAsync(null, ct).ConfigureAwait(false);
            if (events.Count > 0)
            {
                _engine.LoadFromEvents(events);
                _logger.LogInformation("{Count} Events in die Engine geladen.", events.Count);
            }

            _eventChannel = Channel.CreateUnbounded<IReadOnlyList<DomainEvent>>(new UnboundedChannelOptions { SingleReader = true });
            _candleChannel = Channel.CreateUnbounded<IReadOnlyList<Candle>>(new UnboundedChannelOptions { SingleReader = true });
            _engine.EventsAppended += OnEngineEvents;
            _persistTask = Task.Run(() => PersistEventsLoopAsync(_eventChannel.Reader));
            _candleTask = Task.Run(() => PersistCandlesLoopAsync(_candleChannel.Reader));

            EnsureDefaultAccount(settings);

            _hub.EventReceived += OnMarketEvent;
            _changes.WatchlistsChanged += OnWatchlistsChanged;

            await _readModels.InitializeAsync(ct).ConfigureAwait(false);
            await _alertMonitor.StartAsync(ct).ConfigureAwait(false);

            try { await _hub.StartAsync(token).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogError(ex, "Marktdaten konnten nicht gestartet werden."); }

            await SubscribeRelevantSymbolsAsync(token).ConfigureAwait(false);

            _suspended = false;
            _tickTask = Task.Run(() => TickLoopAsync(token));
            _running = true;
            _logger.LogInformation("Laufzeit gestartet.");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_running) return;
            _running = false;
            _cts?.Cancel();
            if (_tickTask is not null) await SafeAwait(_tickTask).ConfigureAwait(false);

            _hub.EventReceived -= OnMarketEvent;
            _changes.WatchlistsChanged -= OnWatchlistsChanged;
            _alertMonitor.Stop();
            try { await _hub.StopAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Marktdaten konnten nicht sauber gestoppt werden."); }

            _engine.EventsAppended -= OnEngineEvents;
            _eventChannel?.Writer.TryComplete();
            _candleChannel?.Writer.TryComplete();
            if (_persistTask is not null) await SafeAwait(_persistTask).ConfigureAwait(false);
            if (_candleTask is not null) await SafeAwait(_candleTask).ConfigureAwait(false);

            _cts?.Dispose();
            _cts = null;
            lock (_subscribeLock) _subscribed.Clear();
            _logger.LogInformation("Laufzeit gestoppt.");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task SuspendAsync(CancellationToken ct = default)
    {
        if (!_running || _suspended) return;
        _suspended = true;
        try { await _hub.SuspendAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Marktdaten konnten nicht in Standby gehen."); }
    }

    public async Task ResumeAsync(CancellationToken ct = default)
    {
        if (!_running || !_suspended) return;
        try { await _hub.ResumeAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Marktdaten konnten nach Standby nicht fortgesetzt werden."); }
        _suspended = false;
        Tick();
    }

    /// <summary>Synchrones Beenden (z. B. wenn der DI-Container synchron entsorgt wird).</summary>
    public void Dispose() => Task.Run(() => DisposeAsync().AsTask()).GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _readModels.Dispose();
    }

    // ------------------------------------------------------------------ Start

    private void EnsureDefaultAccount(AppSettings settings)
    {
        var acc = settings.DefaultAccountId;
        try
        {
            if (_engine.AccountExists(acc)) return;
            var result = _engine.Execute(new OpenAccountRequest
            {
                AccountId = acc,
                Source = CommandSource.Human,
                ClientName = "System",
                Name = _options.DefaultAccountName,
                InitialCash = settings.InitialCash,
                BaseCurrency = settings.BaseCurrency,
                Risk = settings.Risk,
                CryptoFees = settings.CryptoFees,
                StockFees = settings.StockFees,
            });
            if (result.Accepted) _logger.LogInformation("Standardkonto {Account} mit {Cash} {Currency} angelegt.", acc, settings.InitialCash, settings.BaseCurrency);
            else _logger.LogError("Standardkonto {Account} konnte nicht angelegt werden: {Reason}", acc, result.RejectReason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Standardkonto {Account} konnte nicht angelegt werden.", acc);
        }
    }

    private async Task SubscribeRelevantSymbolsAsync(CancellationToken ct)
    {
        var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var wl in await _watchlists.GetAllAsync(ct).ConfigureAwait(false))
                symbols.UnionWith(wl.Symbols);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogWarning(ex, "Watchlists konnten nicht gelesen werden."); }
        try
        {
            foreach (var account in _engine.GetAccounts())
            {
                symbols.UnionWith(_engine.GetPortfolio(account.AccountId).Positions.Where(p => p.Quantity != 0).Select(p => p.Symbol));
                symbols.UnionWith(_engine.GetOrders(account.AccountId, openOnly: true).Select(o => o.Symbol));
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Positionen/Orders konnten nicht gelesen werden."); }
        await SubscribeAsync(symbols, ct).ConfigureAwait(false);
    }

    private async Task SubscribeAsync(IEnumerable<string> symbols, CancellationToken ct)
    {
        List<string> fresh;
        lock (_subscribeLock)
        {
            fresh = symbols.Where(s => !string.IsNullOrWhiteSpace(s) && _subscribed.Add(s)).ToList();
        }
        if (fresh.Count == 0) return;
        try
        {
            await _hub.SubscribeAsync(fresh, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Abonnement für {Symbols} fehlgeschlagen.", string.Join(", ", fresh));
            lock (_subscribeLock) foreach (var s in fresh) _subscribed.Remove(s);
        }
    }

    private void OnWatchlistsChanged(object? sender, EventArgs e)
    {
        var token = _cts?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                var all = await _watchlists.GetAllAsync(token).ConfigureAwait(false);
                await SubscribeAsync(all.SelectMany(w => w.Symbols), token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogWarning(ex, "Watchlist-Abos konnten nicht aktualisiert werden."); }
        }, token);
    }

    // ------------------------------------------------------------------ Engine → Persistence

    private void OnEngineEvents(object? sender, EngineEventsEventArgs e)
    {
        if (e.Events.Count == 0) return;
        if (_eventChannel is null || !_eventChannel.Writer.TryWrite(e.Events))
            _logger.LogError("{Count} Events konnten nicht zur Speicherung eingereiht werden.", e.Events.Count);

        var newSymbols = e.Events.OfType<OrderSubmitted>().Select(s => s.Order.Symbol).ToList();
        if (newSymbols.Count > 0)
        {
            var token = _cts?.Token ?? CancellationToken.None;
            _ = Task.Run(() => SubscribeAsync(newSymbols, token), token);
        }
    }

    private async Task PersistEventsLoopAsync(ChannelReader<IReadOnlyList<DomainEvent>> reader)
    {
        await foreach (var batch in reader.ReadAllAsync().ConfigureAwait(false))
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await _eventStore.AppendAsync(batch).ConfigureAwait(false);
                    break;
                }
                catch (Exception ex) when (attempt < 5)
                {
                    _logger.LogWarning(ex, "Speichern von {Count} Events fehlgeschlagen (Versuch {Attempt}).", batch.Count, attempt);
                    await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogCritical(ex, "{Count} Events konnten nicht gespeichert werden.", batch.Count);
                    break;
                }
            }
        }
    }

    // ------------------------------------------------------------------ Marktdaten → Engine / Kerzen

    private void OnMarketEvent(object? sender, MarketEvent e)
    {
        try { _engine.OnMarketEvent(e); }
        catch (Exception ex) { _logger.LogError(ex, "Engine konnte Marktereignis {Type} für {Symbol} nicht verarbeiten.", e.GetType().Name, e.Symbol); }

        switch (e)
        {
            case CandleUpdate { Candle.IsClosed: true } cu:
                _candleChannel?.Writer.TryWrite([cu.Candle]);
                break;
            case GapDetected { BackfilledCandles.Count: > 0 } gap:
                _candleChannel?.Writer.TryWrite(gap.BackfilledCandles.Where(c => c.IsClosed).ToList());
                break;
        }
    }

    private async Task PersistCandlesLoopAsync(ChannelReader<IReadOnlyList<Candle>> reader)
    {
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            var batch = new List<Candle>();
            while (batch.Count < 1000 && reader.TryRead(out var part)) batch.AddRange(part);
            if (batch.Count == 0) continue;
            try { await _candleStore.UpsertAsync(batch).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogError(ex, "{Count} Kerzen konnten nicht gespeichert werden.", batch.Count); }
        }
    }

    // ------------------------------------------------------------------ Uhr

    private async Task TickLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.ClockTickInterval);
        try
        {
            Tick();
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) Tick();
        }
        catch (OperationCanceledException) { }
    }

    private void Tick()
    {
        if (_suspended) return;
        var now = _clock.UtcNow;
        try { _engine.OnClockTick(now); }
        catch (Exception ex) { _logger.LogError(ex, "Engine-Uhrtakt fehlgeschlagen."); }

        if (now - _lastPrune >= TimeSpan.FromDays(1) && Interlocked.Exchange(ref _pruning, 1) == 0)
        {
            _lastPrune = now;
            _ = Task.Run(async () =>
            {
                try { await _candleStore.PruneAsync(_options.MinuteCandleRetention).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "Aufräumen der 1-Minuten-Kerzen fehlgeschlagen."); }
                finally { Interlocked.Exchange(ref _pruning, 0); }
            });
        }
    }

    private async Task SafeAwait(Task t)
    {
        try { await t.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning(ex, "Hintergrundaufgabe endete mit Fehler."); }
    }
}
