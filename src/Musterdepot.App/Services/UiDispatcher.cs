using System.Windows;
using System.Windows.Threading;
using Musterdepot.Core.ReadModels;

namespace Musterdepot.App.Services;

/// <summary>Marshalt Arbeit auf den UI-Thread.</summary>
public interface IUiDispatcher
{
    bool CheckAccess();
    void Post(Action action);
    Task InvokeAsync(Action action);
}

public sealed class WpfUiDispatcher : IUiDispatcher
{
    private static Dispatcher Dispatcher => Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

    public bool CheckAccess() => Dispatcher.CheckAccess();

    public void Post(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.HasShutdownStarted) return;
        d.BeginInvoke(action, DispatcherPriority.Background);
    }

    public Task InvokeAsync(Action action) => Dispatcher.InvokeAsync(action).Task;
}

/// <summary>
/// Bindet ein Lesemodell (Changed auf Hintergrund-Thread) an den UI-Thread. Mehrere Änderungen innerhalb von
/// <c>throttle</c> werden zu einem Aufruf zusammengefasst – so bleibt die Oberfläche bei vielen Kurs-Ticks ruhig.
/// </summary>
public sealed class ReadModelSubscription<T> : IDisposable
{
    private readonly IReadModel<T> _model;
    private readonly IUiDispatcher _dispatcher;
    private readonly Action<T> _apply;
    private readonly TimeSpan _throttle;
    private int _pending;
    private bool _disposed;

    public ReadModelSubscription(IReadModel<T> model, IUiDispatcher dispatcher, Action<T> apply, TimeSpan throttle, bool applyNow = true)
    {
        _model = model;
        _dispatcher = dispatcher;
        _apply = apply;
        _throttle = throttle;
        _model.Changed += OnChanged;
        if (applyNow) Schedule();
    }

    private void OnChanged(object? sender, EventArgs e) => Schedule();

    private void Schedule()
    {
        if (Interlocked.Exchange(ref _pending, 1) == 1) return;
        if (_throttle <= TimeSpan.Zero)
            _dispatcher.Post(Flush);
        else
            _ = Task.Delay(_throttle).ContinueWith(_ => _dispatcher.Post(Flush), TaskScheduler.Default);
    }

    private void Flush()
    {
        Interlocked.Exchange(ref _pending, 0);
        if (_disposed) return;
        _apply(_model.Current);
    }

    public void Dispose()
    {
        _disposed = true;
        _model.Changed -= OnChanged;
    }
}

public static class ReadModelExtensions
{
    public static IDisposable Subscribe<T>(this IReadModel<T> model, IUiDispatcher dispatcher, Action<T> apply, TimeSpan? throttle = null)
        => new ReadModelSubscription<T>(model, dispatcher, apply, throttle ?? TimeSpan.FromMilliseconds(50));
}
