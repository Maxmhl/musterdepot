using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Musterdepot.Core.Abstractions;

namespace Musterdepot.Mcp.Bridge;

/// <summary>
/// Named-Pipe-Server für die Claude-Desktop-Brücke (<c>Musterdepot.McpBridge.exe</c>).
/// <para>
/// Protokoll pro Verbindung: Die Brücke sendet zuerst <b>eine Handshake-Zeile</b> (UTF-8-JSON, mit <c>\n</c> abgeschlossen):
/// <c>{"type":"musterdepot-bridge","version":1,"token":"mdp_…"}</c>. Der Server antwortet mit einer Zeile
/// <c>{"ok":true,"client_id":"…","name":"…"}</c> oder <c>{"ok":false,"error":"…"}</c> (danach wird getrennt).
/// Ab dann fließen die MCP-Nachrichten (JSON-RPC, zeilenweise wie bei stdio) unverändert in beide Richtungen;
/// der Server betreibt pro Verbindung einen eigenen MCP-Server mit <see cref="StreamServerTransport"/> und denselben Tools.
/// </para>
/// <para>
/// Authentifizierung: Token bevorzugt (wie bei HTTP, gleiche Rechte/Modi). Tokenlos nur, wenn
/// <see cref="McpOptions.AllowTokenlessPipeClient"/> gesetzt ist – dann gilt der Client "claude-desktop" aus den Einstellungen.
/// Das Token wird bei jeder Nachricht erneut geprüft, ein Widerruf wirkt also sofort.
/// </para>
/// <para>
/// Zugriffsschutz: Auf Windows erhält die Pipe eine ACL nur für die SID des aktuellen Benutzers (Vollzugriff) und
/// verweigert Netzwerkzugriffe. Auf anderen Systemen (Entwicklung) wird <see cref="PipeOptions.CurrentUserOnly"/> verwendet.
/// </para>
/// </summary>
public sealed class BridgePipeServer : IAsyncDisposable
{
    public const int HandshakeMaxBytes = 8192;
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    private readonly IServiceProvider _root;
    private readonly McpOptions _options;
    private readonly ITokenService _tokens;
    private readonly ISettingsStore _settings;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<int, Task> _connections = new();
    private readonly Lock _lock = new();
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private int _nextId;

    public BridgePipeServer(IServiceProvider root, IOptions<McpOptions> options, ITokenService tokens, ISettingsStore settings, ILoggerFactory? loggerFactory = null)
    {
        _root = root;
        _options = options.Value;
        _tokens = tokens;
        _settings = settings;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<BridgePipeServer>();
    }

    public string PipeName => _options.PipeName;
    public bool IsRunning { get { lock (_lock) return _acceptLoop is { IsCompleted: false }; } }
    public int ActiveConnections => _connections.Count;

    public void Start()
    {
        lock (_lock)
        {
            if (_acceptLoop is { IsCompleted: false }) return;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(ct), CancellationToken.None);
        }
        _logger.LogInformation("MCP-Brücke wartet auf Named Pipe '{Pipe}'", PipeName);
    }

    public async Task StopAsync()
    {
        Task? loop;
        CancellationTokenSource? cts;
        lock (_lock)
        {
            loop = _acceptLoop;
            cts = _cts;
            _acceptLoop = null;
            _cts = null;
        }
        if (cts is null) return;
        await cts.CancelAsync().ConfigureAwait(false);
        try { if (loop is not null) await loop.ConfigureAwait(false); } catch { /* beendet */ }
        try { await Task.WhenAll(_connections.Values).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { /* beendet */ }
        cts.Dispose();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try { pipe = CreatePipe(); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Named Pipe '{Pipe}' konnte nicht erstellt werden", PipeName);
                try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                _logger.LogWarning(ex, "Fehler beim Warten auf Pipe-Verbindung");
                continue;
            }

            var id = Interlocked.Increment(ref _nextId);
            var task = Task.Run(async () =>
            {
                try { await HandleConnectionAsync(pipe, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _logger.LogWarning(ex, "Pipe-Verbindung {Id} mit Fehler beendet", id); }
                finally
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    _connections.TryRemove(id, out _);
                }
            }, CancellationToken.None);
            _connections[id] = task;
        }
    }

    private NamedPipeServerStream CreatePipe()
    {
        if (OperatingSystem.IsWindows())
            return CreateWindowsPipe(PipeName);
        return new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    [SupportedOSPlatform("windows")]
    private static NamedPipeServerStream CreateWindowsPipe(string name)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("SID des aktuellen Benutzers unbekannt.");
        var security = new PipeSecurity();
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        // 1) Handshake-Zeile lesen (byteweise, damit nichts vom MCP-Strom verschluckt wird).
        string? line;
        using (var hsCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            hsCts.CancelAfter(HandshakeTimeout);
            try { line = await ReadLineAsync(pipe, HandshakeMaxBytes, hsCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await WriteHandshakeReplyAsync(pipe, new { ok = false, error = "Zeitüberschreitung beim Handshake." }, ct).ConfigureAwait(false);
                return;
            }
        }
        if (line is null) return;

        string? token = null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String)
                token = t.GetString();
        }
        catch (JsonException)
        {
            await WriteHandshakeReplyAsync(pipe, new { ok = false, error = "Ungültiger Handshake (JSON-Zeile mit 'token' erwartet)." }, ct).ConfigureAwait(false);
            return;
        }

        Func<CancellationToken, Task<ClientIdentity?>> resolve;
        if (!string.IsNullOrWhiteSpace(token))
            resolve = c => _tokens.ValidateAsync(token, c);
        else if (_options.AllowTokenlessPipeClient)
            resolve = ResolveTokenlessClientAsync;
        else
        {
            await WriteHandshakeReplyAsync(pipe, new { ok = false, error = "Token fehlt. Brücke mit --token <token> oder Umgebungsvariable MUSTERDEPOT_TOKEN starten." }, ct).ConfigureAwait(false);
            return;
        }

        var identity = await resolve(ct).ConfigureAwait(false);
        if (identity is null)
        {
            _logger.LogWarning("Pipe-Verbindung abgelehnt: Token ungültig oder Client deaktiviert");
            await WriteHandshakeReplyAsync(pipe, new { ok = false, error = "Token ungültig, widerrufen oder Client deaktiviert." }, ct).ConfigureAwait(false);
            return;
        }
        await WriteHandshakeReplyAsync(pipe, new { ok = true, client_id = identity.ClientId, name = identity.Name }, ct).ConfigureAwait(false);
        _logger.LogInformation("Brücke verbunden: Client {ClientId} ({Name})", identity.ClientId, identity.Name);

        // 2) MCP-Server über den Pipe-Stream mit denselben Tools.
        var options = McpServerSetup.CreateStreamServerOptions(_options);
        options.Filters.Message.IncomingFilters.Add(next => async (context, cancel) =>
        {
            var current = await resolve(cancel).ConfigureAwait(false); // Widerruf/Deaktivierung wirkt sofort
            context.User = current is null ? null : McpCaller.ToPrincipal(current);
            await next(context, cancel).ConfigureAwait(false);
        });

        await using var transport = new StreamServerTransport(pipe, pipe, "musterdepot-pipe", _loggerFactory);
        await using var server = McpServer.Create(transport, options, _loggerFactory, _root);
        await server.RunAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Brücke getrennt: Client {ClientId}", identity.ClientId);
    }

    private async Task<ClientIdentity?> ResolveTokenlessClientAsync(CancellationToken ct)
    {
        var settings = await _settings.LoadAsync(ct).ConfigureAwait(false);
        var name = _options.TokenlessPipeClientName;
        var c = settings.AiClients.FirstOrDefault(x => string.Equals(x.ClientId, name, StringComparison.OrdinalIgnoreCase))
                ?? settings.AiClients.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (c is null || !c.Enabled) return null;
        var current = (_root.GetService(typeof(IClientRegistry)) as IClientRegistry)?.Get(c.ClientId) ?? c;
        return current.Enabled ? new ClientIdentity(current.ClientId, current.Name, current.Mode, current.Scopes) : null;
    }

    /// <summary>Liest bis <c>\n</c> (ohne Zeilenende); null bei Verbindungsende.</summary>
    internal static async Task<string?> ReadLineAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        var buffer = new byte[1];
        using var ms = new MemoryStream();
        while (true)
        {
            var n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n == 0) return ms.Length == 0 ? null : Encoding.UTF8.GetString(ms.ToArray());
            if (buffer[0] == (byte)'\n') break;
            if (ms.Length >= maxBytes) throw new InvalidDataException("Handshake-Zeile zu lang.");
            ms.WriteByte(buffer[0]);
        }
        return Encoding.UTF8.GetString(ms.ToArray()).TrimEnd('\r');
    }

    private static async Task WriteHandshakeReplyAsync(Stream stream, object reply, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reply) + "\n");
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }
}
