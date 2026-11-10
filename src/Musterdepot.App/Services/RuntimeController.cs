using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Musterdepot.App.Hosting;

namespace Musterdepot.App.Services;

/// <summary>
/// Startet/stoppt Laufzeit (Marktdaten → Engine) und MCP-Server, reagiert auf Standby und zeigt den Zustand an.
/// Fehler werden angezeigt und protokolliert, aber nie bis zum Absturz durchgereicht.
/// </summary>
public sealed partial class RuntimeController(
    AppServices services,
    BootstrapResult bootstrap,
    IUiDispatcher ui,
    ILogger<RuntimeController> logger) : ObservableObject
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    [ObservableProperty] private string _runtimeStatus = "nicht gestartet";
    [ObservableProperty] private string _mcpStatus = "aus";
    [ObservableProperty] private bool _runtimeRunning;
    [ObservableProperty] private bool _mcpRunning;
    [ObservableProperty] private string? _lastError;

    public async Task StartAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (services.Runtime is null)
            {
                SetStatus(runtime: "nicht verfügbar (Laufzeit fehlt)");
            }
            else
            {
                SetStatus(runtime: "startet …");
                try
                {
                    await Task.Run(() => services.Runtime.StartAsync());
                    SetStatus(runtime: "läuft", running: true);
                    logger.LogInformation("Laufzeit gestartet");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Laufzeit konnte nicht gestartet werden");
                    SetStatus(runtime: "Fehler beim Start", error: "Laufzeit: " + ex.GetBaseException().Message);
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        if (bootstrap.Settings.McpEnabled) await StartMcpAsync();
    }

    public async Task StartMcpAsync()
    {
        if (services.McpHost is null)
        {
            SetMcp("nicht verfügbar (MCP-Server fehlt)", false);
            return;
        }
        await _gate.WaitAsync();
        try
        {
            if (services.McpHost.IsRunning) { SetMcp(Describe(), true); return; }
            SetMcp("startet …", false);
            await Task.Run(() => services.McpHost.StartAsync());
            SetMcp(Describe(), services.McpHost.IsRunning);
            logger.LogInformation("MCP-Server gestartet: {Endpoint}", services.McpHost.Endpoint);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MCP-Server konnte nicht gestartet werden");
            SetMcp("Fehler: " + ex.GetBaseException().Message, false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopMcpAsync()
    {
        if (services.McpHost is null) return;
        await _gate.WaitAsync();
        try
        {
            if (services.McpHost.IsRunning) await Task.Run(() => services.McpHost.StopAsync());
            SetMcp("aus", false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MCP-Server konnte nicht gestoppt werden");
            SetMcp("Fehler beim Stoppen: " + ex.GetBaseException().Message, services.McpHost.IsRunning);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RestartMcpAsync(bool enabled)
    {
        await StopMcpAsync();
        if (enabled) await StartMcpAsync();
    }

    public async Task SuspendAsync()
    {
        if (services.Runtime is null) return;
        try
        {
            logger.LogInformation("Standby: Laufzeit wird angehalten");
            await Task.Run(() => services.Runtime.SuspendAsync());
            SetStatus(runtime: "Standby", running: false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fehler beim Anhalten (Standby)");
        }
    }

    public async Task ResumeAsync()
    {
        if (services.Runtime is null) return;
        try
        {
            logger.LogInformation("Aufwachen: Laufzeit verbindet neu und lädt Lücken nach");
            SetStatus(runtime: "verbindet neu …");
            await Task.Run(() => services.Runtime.ResumeAsync());
            SetStatus(runtime: "läuft", running: true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fehler beim Aufwachen");
            SetStatus(runtime: "Fehler nach Standby", error: "Aufwachen: " + ex.GetBaseException().Message);
        }
    }

    /// <summary>Beim Beenden: MCP-Server und Laufzeit sauber stoppen.</summary>
    public async Task ShutdownAsync()
    {
        try
        {
            if (services.McpHost is { IsRunning: true } mcp) await mcp.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MCP-Server ließ sich nicht sauber stoppen");
        }
        try
        {
            if (services.Runtime is { IsRunning: true } rt) await rt.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Laufzeit ließ sich nicht sauber stoppen");
        }
    }

    private string Describe() => services.McpHost?.Endpoint is { } ep ? $"läuft auf {ep}" : "läuft";

    private void SetStatus(string runtime, bool? running = null, string? error = null) => ui.Post(() =>
    {
        RuntimeStatus = runtime;
        if (running is { } r) RuntimeRunning = r;
        if (error is not null) LastError = error;
    });

    private void SetMcp(string status, bool running) => ui.Post(() =>
    {
        McpStatus = status;
        McpRunning = running;
    });
}
