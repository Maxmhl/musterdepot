using Microsoft.Extensions.Logging;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;
using Musterdepot.Core.ReadModels;

namespace Musterdepot.Commands;

/// <summary>
/// Hält alle Lesemodelle der Oberfläche aktuell:
/// Engine-Events → Portfolio/Orders/Fills/Equity/Not-Aus (zusammengefasst im Hintergrund);
/// Kursereignisse → Quotes und Portfolio gedrosselt (<see cref="CommandOptions.QuoteThrottleInterval"/>),
/// <see cref="MarketEventReceived"/> pro Tick; Verbindungsstatus; Protokoll, Freigaben, Einstellungen, Watchlists, Alarme.
/// </summary>
public sealed class ReadModels : IReadModels, IDisposable
{
    private readonly ITradingEngine _engine;
    private readonly IMarketDataHub _hub;
    private readonly IAuditLog _audit;
    private readonly IApprovalService _approvals;
    private readonly SettingsService _settings;
    private readonly IWatchlistStore _watchlists;
    private readonly IAlertStore _alerts;
    private readonly StoreChangeNotifier _changes;
    private readonly IClock _clock;
    private readonly CommandOptions _options;
    private readonly ILogger<ReadModels> _logger;
    private readonly Timer _quoteTimer;
    private readonly Lock _auditLock = new();
    private int _quotesDirty;
    private int _engineRefreshScheduled;
    private int _disposed;

    private readonly ReadModel<PortfolioSnapshot> _portfolio;
    private readonly ReadModel<IReadOnlyList<Order>> _openOrders = new([]);
    private readonly ReadModel<IReadOnlyList<Order>> _allOrders = new([]);
    private readonly ReadModel<IReadOnlyList<Fill>> _fills = new([]);
    private readonly ReadModel<IReadOnlyList<EquitySnapshot>> _equity = new([]);
    private readonly ReadModel<IReadOnlyDictionary<string, QuoteSnapshot>> _quotes = new(new Dictionary<string, QuoteSnapshot>());
    private readonly ReadModel<IReadOnlyList<Watchlist>> _watchlistModel = new([]);
    private readonly ReadModel<IReadOnlyList<PriceAlert>> _alertModel = new([]);
    private readonly ReadModel<IReadOnlyList<AuditEntry>> _auditModel = new([]);
    private readonly ReadModel<IReadOnlyList<ApprovalRequest>> _pending = new([]);
    private readonly ReadModel<IReadOnlyDictionary<string, ConnectionState>> _connections = new(new Dictionary<string, ConnectionState>());
    private readonly ReadModel<AppSettings> _settingsModel = new(new AppSettings());
    private readonly ReadModel<bool> _killSwitch = new(false);

    public ReadModels(
        ITradingEngine engine,
        IMarketDataHub hub,
        IAuditLog audit,
        IApprovalService approvals,
        SettingsService settings,
        IWatchlistStore watchlists,
        IAlertStore alerts,
        StoreChangeNotifier changes,
        IClock clock,
        CommandOptions options,
        ILogger<ReadModels> logger)
    {
        _engine = engine;
        _hub = hub;
        _audit = audit;
        _approvals = approvals;
        _settings = settings;
        _watchlists = watchlists;
        _alerts = alerts;
        _changes = changes;
        _clock = clock;
        _options = options;
        _logger = logger;
        _portfolio = new ReadModel<PortfolioSnapshot>(TradingQueries.EmptyPortfolio("", clock.UtcNow));

        _engine.EventsAppended += OnEngineEvents;
        _hub.EventReceived += OnMarketEvent;
        _audit.EntryAppended += OnAuditEntry;
        _approvals.RequestAdded += OnApprovalChanged;
        _approvals.RequestResolved += OnApprovalChanged;
        _settings.Changed += OnSettingsChanged;
        _changes.WatchlistsChanged += OnWatchlistsChanged;
        _changes.AlertsChanged += OnAlertsChanged;
        _quoteTimer = new Timer(_ => FlushQuotes(), null, options.QuoteThrottleInterval, options.QuoteThrottleInterval);
    }

    public IReadModel<PortfolioSnapshot> Portfolio => _portfolio;
    public IReadModel<IReadOnlyList<Order>> OpenOrders => _openOrders;
    public IReadModel<IReadOnlyList<Order>> AllOrders => _allOrders;
    public IReadModel<IReadOnlyList<Fill>> Fills => _fills;
    public IReadModel<IReadOnlyList<EquitySnapshot>> EquityCurve => _equity;
    public IReadModel<IReadOnlyDictionary<string, QuoteSnapshot>> Quotes => _quotes;
    public IReadModel<IReadOnlyList<Watchlist>> Watchlists => _watchlistModel;
    public IReadModel<IReadOnlyList<PriceAlert>> Alerts => _alertModel;
    public IReadModel<IReadOnlyList<AuditEntry>> AuditLog => _auditModel;
    public IReadModel<IReadOnlyList<ApprovalRequest>> PendingApprovals => _pending;
    public IReadModel<IReadOnlyDictionary<string, ConnectionState>> Connections => _connections;
    public IReadModel<AppSettings> Settings => _settingsModel;
    public IReadModel<bool> KillSwitch => _killSwitch;

    public event EventHandler<MarketEvent>? MarketEventReceived;

    /// <summary>Alle Lesemodelle neu laden (beim Start der Laufzeit).</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        _settingsModel.Set(_settings.Current);
        RefreshEngineModels();
        RefreshQuotes();
        RefreshConnections();
        _pending.Set(_approvals.Pending);
        await RefreshWatchlistsAsync(ct).ConfigureAwait(false);
        await RefreshAlertsAsync(ct).ConfigureAwait(false);
        try
        {
            var entries = await _audit.QueryAsync(null, null, _options.AuditReadModelSize, ct).ConfigureAwait(false);
            lock (_auditLock) _auditModel.Set(entries);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogWarning(ex, "Protokoll konnte nicht geladen werden."); }
    }

    /// <summary>Portfolio, Orders, Fills, Equity und Not-Aus aus der Engine neu lesen.</summary>
    public void RefreshEngineModels()
    {
        try
        {
            var acc = _settings.Current.DefaultAccountId;
            if (!_engine.AccountExists(acc))
            {
                _portfolio.Set(TradingQueries.EmptyPortfolio(acc, _clock.UtcNow));
                return;
            }
            var all = _engine.GetOrders(acc);
            _portfolio.Set(_engine.GetPortfolio(acc));
            _allOrders.Set(all);
            _openOrders.Set(all.Where(o => o.IsOpen).ToList());
            _fills.Set(_engine.GetFills(acc));
            _equity.Set(_engine.GetEquityCurve(acc));
            var ks = _engine.IsKillSwitchEngaged(acc);
            if (_killSwitch.Current != ks) _killSwitch.Set(ks);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lesemodelle der Engine konnten nicht aktualisiert werden.");
        }
    }

    /// <summary>Gedrosselte Kursaktualisierung (vom Timer aufgerufen; für Tests öffentlich).</summary>
    public void FlushQuotes()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _quotesDirty, 0) == 0) return;
        RefreshQuotes();
        try
        {
            var acc = _settings.Current.DefaultAccountId;
            if (_engine.AccountExists(acc)) _portfolio.Set(_engine.GetPortfolio(acc));
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Portfolio-Bewertung fehlgeschlagen."); }
    }

    private void RefreshQuotes()
    {
        try { _quotes.Set(new Dictionary<string, QuoteSnapshot>(_hub.GetAllQuotes())); }
        catch (Exception ex) { _logger.LogDebug(ex, "Kurse nicht lesbar."); }
    }

    private void RefreshConnections()
    {
        try { _connections.Set(new Dictionary<string, ConnectionState>(_hub.GetConnectionStates())); }
        catch (Exception ex) { _logger.LogDebug(ex, "Verbindungsstatus nicht lesbar."); }
    }

    private async Task RefreshWatchlistsAsync(CancellationToken ct = default)
    {
        try { _watchlistModel.Set(await _watchlists.GetAllAsync(ct).ConfigureAwait(false)); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogWarning(ex, "Watchlists konnten nicht geladen werden."); }
    }

    private async Task RefreshAlertsAsync(CancellationToken ct = default)
    {
        try { _alertModel.Set(await _alerts.GetAllAsync(ct).ConfigureAwait(false)); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogWarning(ex, "Alarme konnten nicht geladen werden."); }
    }

    private void OnEngineEvents(object? sender, EngineEventsEventArgs e)
    {
        // Zusammenfassen und außerhalb des Engine-Aufrufs lesen (keine Reentranz in die Engine).
        if (Interlocked.Exchange(ref _engineRefreshScheduled, 1) == 1) return;
        _ = Task.Run(() =>
        {
            Interlocked.Exchange(ref _engineRefreshScheduled, 0);
            RefreshEngineModels();
        });
    }

    private void OnMarketEvent(object? sender, MarketEvent e)
    {
        switch (e)
        {
            case QuoteTick or TradeTick or OrderBookUpdate:
                Interlocked.Exchange(ref _quotesDirty, 1);
                break;
            case ConnectionStatusChanged:
                RefreshConnections();
                break;
        }
        try { MarketEventReceived?.Invoke(this, e); }
        catch (Exception ex) { _logger.LogWarning(ex, "Handler für MarketEventReceived fehlgeschlagen."); }
    }

    private void OnAuditEntry(object? sender, AuditEntry entry)
    {
        lock (_auditLock)
        {
            var list = new List<AuditEntry>(_auditModel.Current.Count + 1) { entry };
            list.AddRange(_auditModel.Current.Take(_options.AuditReadModelSize - 1));
            _auditModel.Set(list);
        }
    }

    private void OnApprovalChanged(object? sender, ApprovalRequest e) => _pending.Set(_approvals.Pending);

    private void OnSettingsChanged(object? sender, AppSettings s)
    {
        _settingsModel.Set(s);
        OnEngineEvents(this, new EngineEventsEventArgs([]));
    }

    private void OnWatchlistsChanged(object? sender, EventArgs e) => _ = RefreshWatchlistsAsync();

    private void OnAlertsChanged(object? sender, EventArgs e) => _ = RefreshAlertsAsync();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _quoteTimer.Dispose();
        _engine.EventsAppended -= OnEngineEvents;
        _hub.EventReceived -= OnMarketEvent;
        _audit.EntryAppended -= OnAuditEntry;
        _approvals.RequestAdded -= OnApprovalChanged;
        _approvals.RequestResolved -= OnApprovalChanged;
        _settings.Changed -= OnSettingsChanged;
        _changes.WatchlistsChanged -= OnWatchlistsChanged;
        _changes.AlertsChanged -= OnAlertsChanged;
    }
}
