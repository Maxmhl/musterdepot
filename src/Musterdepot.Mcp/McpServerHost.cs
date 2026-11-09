using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Musterdepot.Core.Abstractions;
using Musterdepot.Mcp.Bridge;
using Musterdepot.Mcp.Security;
using Musterdepot.Mcp.Tools;

namespace Musterdepot.Mcp;

/// <summary>
/// MCP-Server im Programmprozess: eigener Kestrel-<see cref="WebApplication"/>, gebunden <b>nur an 127.0.0.1</b>,
/// Endpoint <c>/mcp</c> (Streamable HTTP, zustandslos). Startet zusätzlich den <see cref="BridgePipeServer"/> für Claude Desktop.
/// Die Tools verwenden über <see cref="McpToolBackend"/> die Singletons des Root-Containers der App
/// (gleiche ICommandBus-/Engine-Instanz wie die Oberfläche).
/// </summary>
public sealed class McpServerHost : IMcpServerHost, IAsyncDisposable
{
    private readonly IServiceProvider _root;
    private readonly McpOptions _options;
    private readonly ISettingsStore _settings;
    private readonly ITokenService _tokens;
    private readonly BridgePipeServer _pipe;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WebApplication? _app;

    public McpServerHost(IServiceProvider root, IOptions<McpOptions> options, ISettingsStore settings, ITokenService tokens, BridgePipeServer pipe, ILoggerFactory? loggerFactory = null)
    {
        _root = root;
        _options = options.Value;
        _settings = settings;
        _tokens = tokens;
        _pipe = pipe;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<McpServerHost>();
    }

    public bool IsRunning => _app is not null || _pipe.IsRunning;
    public Uri? Endpoint { get; private set; }
    /// <summary>Tatsächlich gebundener Port (nach <see cref="StartAsync"/>).</summary>
    public int? Port { get; private set; }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsRunning) return;
            var settings = await _settings.LoadAsync(ct).ConfigureAwait(false);
            if (!settings.McpEnabled)
            {
                _logger.LogInformation("MCP-Server ist in den Einstellungen deaktiviert und wird nicht gestartet.");
                return;
            }

            if (_options.EnableHttp)
            {
                var port = _options.Port ?? settings.McpPort;
                if (port != 0 && port is <= 1024 or > 65535)
                    throw new InvalidOperationException($"Ungültiger MCP-Port {port}: erlaubt sind 1025–65535 (0 = zufällig).");
                var app = BuildApp(port);
                try
                {
                    await app.StartAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    await app.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
                var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
                              ?? throw new InvalidOperationException("Kestrel meldet keine Adresse.");
                Port = new Uri(address).Port;
                Endpoint = new Uri($"http://127.0.0.1:{Port}/mcp");
                // Host-Prüfung braucht den tatsächlichen Port (bei Port 0 erst jetzt bekannt).
                _guard = null;
                Volatile.Write(ref _guardPort, Port.Value);
                _app = app;
                _logger.LogInformation("MCP-Server läuft auf {Endpoint}", Endpoint);
            }

            if (_options.EnablePipe)
                _pipe.Start();
        }
        finally { _gate.Release(); }
    }

    private int _guardPort;
    private LocalRequestGuard? _guard;

    private WebApplication BuildApp(int port)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(McpServerHost).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.UseKestrelCore();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Listen(IPAddress.Loopback, port); // nur 127.0.0.1, nie 0.0.0.0
        });
        builder.Services.AddRouting();
        builder.Services.AddLogging();
        builder.Services.AddSingleton(_loggerFactory); // Logs der App verwenden (Instanz → wird nicht mit entsorgt)

        // Dieselben Instanzen wie im Root-Container der App.
        builder.Services.AddSingleton(_root.GetRequiredService<McpToolBackend>());
        builder.Services.AddSingleton(_tokens);

        builder.Services.AddMcpServer(o =>
            {
                o.ServerInfo = McpServerSetup.ServerInfo(_options);
                o.ServerInstructions = McpServerSetup.Instructions;
            })
            .WithHttpTransport(t => t.SessionMode = _options.SessionMode)
            .WithTools(McpServerSetup.Tools);

        var app = builder.Build();
        var guardLogger = _loggerFactory.CreateLogger<LocalRequestGuard>();
        var auth = new BearerTokenAuthenticator(_tokens, _loggerFactory.CreateLogger<BearerTokenAuthenticator>());
        app.Use((ctx, next) =>
        {
            // Bei Port 0 steht der echte Port erst nach dem Start fest – Guard daher beim ersten Request erzeugen.
            var p = Volatile.Read(ref _guardPort);
            if (p == 0)
            {
                ctx.Response.StatusCode = Microsoft.AspNetCore.Http.StatusCodes.Status503ServiceUnavailable;
                return Task.CompletedTask;
            }
            var guard = _guard ??= new LocalRequestGuard(p, _options.AdditionalAllowedHosts, _options.AdditionalAllowedOrigins, guardLogger);
            return guard.InvokeAsync(ctx, next);
        });
        app.Use((ctx, next) => auth.InvokeAsync(ctx, next));
        app.MapMcp("/mcp");
        return app;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _pipe.StopAsync().ConfigureAwait(false);
            if (_app is { } app)
            {
                _app = null;
                try { await app.StopAsync(ct).ConfigureAwait(false); }
                finally { await app.DisposeAsync().ConfigureAwait(false); }
                _logger.LogInformation("MCP-Server gestoppt");
            }
            _guard = null;
            Volatile.Write(ref _guardPort, 0);
            Endpoint = null;
            Port = null;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
