namespace Musterdepot.App.Hosting;

/// <summary>
/// Single-Instance per benanntem Mutex (pro Windows-Sitzung). Eine zweite Instanz weckt die erste über ein
/// benanntes Ereignis und beendet sich.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly bool _owns;
    private RegisteredWaitHandle? _registration;

    public SingleInstanceGuard(string name)
    {
        var user = Environment.UserName.Replace('\\', '_');
        _mutex = new Mutex(initiallyOwned: true, $@"Local\{name}.{user}.Instance", out _owns);
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.{user}.Activate");
    }

    public bool IsFirstInstance => _owns;

    /// <summary>Weckt die laufende Instanz (Hauptfenster zeigen).</summary>
    public void SignalFirstInstance()
    {
        try { _activate.Set(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Ruft <paramref name="onActivate"/> auf, sobald eine zweite Instanz gestartet wird (Thread-Pool-Thread).</summary>
    public void ListenForActivation(Action onActivate)
    {
        _registration = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        if (_owns)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        }
        _mutex.Dispose();
        _activate.Dispose();
    }
}
