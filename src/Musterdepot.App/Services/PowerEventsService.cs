using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Musterdepot.App.Services;

/// <summary>Standby/Aufwachen → <see cref="RuntimeController.SuspendAsync"/> / <see cref="RuntimeController.ResumeAsync"/>.</summary>
public sealed class PowerEventsService(RuntimeController runtime, ILogger<PowerEventsService> logger) : IDisposable
{
    private bool _attached;

    public void Attach()
    {
        if (_attached) return;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _attached = true;
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        logger.LogInformation("Energiestatus: {Mode}", e.Mode);
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                // Windows gibt nur kurz Zeit – synchron warten, damit die Uhrzeit gespeichert und sauber getrennt wird.
                try { runtime.SuspendAsync().Wait(TimeSpan.FromSeconds(5)); }
                catch (Exception ex) { logger.LogWarning(ex, "Standby-Vorbereitung unvollständig"); }
                break;
            case PowerModes.Resume:
                _ = runtime.ResumeAsync();
                break;
        }
    }

    public void Dispose()
    {
        if (_attached) SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _attached = false;
    }
}
