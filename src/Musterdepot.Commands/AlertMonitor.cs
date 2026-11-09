using System.Globalization;
using Microsoft.Extensions.Logging;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Commands;
using Musterdepot.Core.MarketData;

namespace Musterdepot.Commands;

/// <summary>
/// Prüft aktive Preisalarme bei jedem Kurs/Trade, markiert ausgelöste Alarme (TriggeredUtc) im Speicher und
/// benachrichtigt <see cref="IUserNotifier"/>. Jeder Alarm löst genau einmal aus.
/// </summary>
public sealed class AlertMonitor : IDisposable
{
    private readonly IMarketDataHub _hub;
    private readonly IAlertStore _store;
    private readonly StoreChangeNotifier _changes;
    private readonly IUserNotifier _notifier;
    private readonly IClock _clock;
    private readonly ILogger<AlertMonitor> _logger;
    private readonly Lock _lock = new();
    private Dictionary<string, List<PriceAlert>> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _triggeredIds = new(StringComparer.Ordinal);
    private bool _started;

    public AlertMonitor(IMarketDataHub hub, IAlertStore store, StoreChangeNotifier changes, IUserNotifier notifier, IClock clock, ILogger<AlertMonitor> logger)
    {
        _hub = hub;
        _store = store;
        _changes = changes;
        _notifier = notifier;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Nach dem Auslösen und Speichern eines Alarms.</summary>
    public event EventHandler<PriceAlert>? AlertTriggered;

    public async Task StartAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_started) return;
            _started = true;
        }
        _hub.EventReceived += OnMarketEvent;
        _changes.AlertsChanged += OnAlertsChanged;
        await ReloadAsync(ct).ConfigureAwait(false);
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!_started) return;
            _started = false;
        }
        _hub.EventReceived -= OnMarketEvent;
        _changes.AlertsChanged -= OnAlertsChanged;
    }

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        try
        {
            var all = await _store.GetAllAsync(ct).ConfigureAwait(false);
            HashSet<string> triggered;
            lock (_lock) triggered = [.. _triggeredIds];
            var map = all.Where(a => a.IsActive && !triggered.Contains(a.AlertId))
                .GroupBy(a => a.Symbol, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            lock (_lock) _active = map;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Alarme konnten nicht geladen werden.");
        }
    }

    private void OnAlertsChanged(object? sender, EventArgs e) => _ = ReloadAsync();

    private void OnMarketEvent(object? sender, MarketEvent e)
    {
        decimal? price;
        decimal? changePercent = null;
        switch (e)
        {
            case TradeTick t: price = t.Price; break;
            case QuoteTick q: price = q.Last ?? (q.Bid is { } b && q.Ask is { } a ? (a + b) / 2m : null); changePercent = q.ChangePercent; break;
            default: return;
        }
        _ = CheckAsync(e.Symbol, price, changePercent);
    }

    /// <summary>Prüft die Alarme eines Symbols gegen Kurs/Änderung (für Tests öffentlich).</summary>
    public async Task<IReadOnlyList<PriceAlert>> CheckAsync(string symbol, decimal? price, decimal? changePercent)
    {
        List<PriceAlert> hits;
        lock (_lock)
        {
            if (!_active.TryGetValue(symbol, out var list) || list.Count == 0) return [];
            hits = list.Where(a => IsHit(a, price, changePercent)).ToList();
            if (hits.Count == 0) return [];
            list.RemoveAll(hits.Contains);
            foreach (var h in hits) _triggeredIds.Add(h.AlertId);
        }
        var triggered = new List<PriceAlert>();
        foreach (var alert in hits)
        {
            var done = alert with { TriggeredUtc = _clock.UtcNow };
            try
            {
                await _store.SaveAsync(done).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Alarm {Id} konnte nicht gespeichert werden.", alert.AlertId);
            }
            triggered.Add(done);
            try
            {
                var value = alert.Condition is AlertCondition.ChangePercentAbove or AlertCondition.ChangePercentBelow
                    ? $"{changePercent?.ToString("0.##", CultureInfo.GetCultureInfo("de-DE"))} %"
                    : price?.ToString("0.########", CultureInfo.GetCultureInfo("de-DE"));
                _notifier.Notify($"Preisalarm {alert.Symbol}", $"{Describe(alert)} – aktuell {value}" + (alert.Note is { Length: > 0 } n ? $" ({n})" : ""));
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Benachrichtigung für Alarm {Id} fehlgeschlagen.", alert.AlertId); }
            try { AlertTriggered?.Invoke(this, done); }
            catch (Exception ex) { _logger.LogWarning(ex, "Handler für AlertTriggered fehlgeschlagen."); }
        }
        // Lesemodelle aktualisieren; ReloadAsync liest nur noch aktive Alarme.
        _changes.RaiseAlertsChanged();
        return triggered;
    }

    public static bool IsHit(PriceAlert a, decimal? price, decimal? changePercent) => a.Condition switch
    {
        AlertCondition.PriceAbove => price >= a.Threshold,
        AlertCondition.PriceBelow => price <= a.Threshold,
        AlertCondition.ChangePercentAbove => changePercent >= a.Threshold,
        AlertCondition.ChangePercentBelow => changePercent <= a.Threshold,
        _ => false,
    };

    private static string Describe(PriceAlert a)
    {
        var de = CultureInfo.GetCultureInfo("de-DE");
        return a.Condition switch
        {
            AlertCondition.PriceAbove => $"Kurs ≥ {a.Threshold.ToString(de)}",
            AlertCondition.PriceBelow => $"Kurs ≤ {a.Threshold.ToString(de)}",
            AlertCondition.ChangePercentAbove => $"Änderung ≥ {a.Threshold.ToString(de)} %",
            _ => $"Änderung ≤ {a.Threshold.ToString(de)} %",
        };
    }

    public void Dispose() => Stop();
}
