using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;

namespace Musterdepot.Commands;

/// <summary>
/// Zwischenspeicher für <see cref="AppSettings"/> über <see cref="ISettingsStore"/> mit Änderungsereignis.
/// Alle Schreibzugriffe der Befehlsschicht laufen hierüber (serialisiert).
/// </summary>
public sealed class SettingsService(ISettingsStore store)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _lock = new();
    private AppSettings? _current;

    /// <summary>Nach jedem Speichern (auf dem aufrufenden Thread).</summary>
    public event EventHandler<AppSettings>? Changed;

    /// <summary>Aktuelle Einstellungen; lädt beim ersten Zugriff synchron.</summary>
    public AppSettings Current
    {
        get
        {
            lock (_lock)
            {
                if (_current is not null) return _current;
            }
            var loaded = Task.Run(() => store.LoadAsync()).GetAwaiter().GetResult();
            lock (_lock) return _current ??= loaded;
        }
    }

    /// <summary>Neu aus dem Speicher laden (beim Start).</summary>
    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        var s = await store.LoadAsync(ct).ConfigureAwait(false);
        lock (_lock) _current = s;
        return s;
    }

    public async Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken ct = default)
    {
        AppSettings next;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            AppSettings? cur;
            lock (_lock) cur = _current;
            cur ??= await store.LoadAsync(ct).ConfigureAwait(false);
            next = update(cur);
            await store.SaveAsync(next, ct).ConfigureAwait(false);
            lock (_lock) _current = next;
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke(this, next);
        return next;
    }

    public Task<AppSettings> SaveAsync(AppSettings settings, CancellationToken ct = default) => UpdateAsync(_ => settings, ct);
}

/// <summary>Meldet Änderungen an Watchlists und Alarmen (für Lesemodelle, Abos, Alarmüberwachung).</summary>
public sealed class StoreChangeNotifier
{
    public event EventHandler? WatchlistsChanged;
    public event EventHandler? AlertsChanged;
    public void RaiseWatchlistsChanged() => WatchlistsChanged?.Invoke(this, EventArgs.Empty);
    public void RaiseAlertsChanged() => AlertsChanged?.Invoke(this, EventArgs.Empty);
}
