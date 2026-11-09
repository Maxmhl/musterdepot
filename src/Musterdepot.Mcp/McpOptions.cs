using ModelContextProtocol.AspNetCore;
using Musterdepot.Core.Abstractions;

namespace Musterdepot.Mcp;

/// <summary>Optionen für MCP-Server und Brücke (über <c>AddMusterdepotMcp(o => …)</c>).</summary>
public sealed class McpOptions
{
    /// <summary>
    /// Port-Override. <c>null</c> = <see cref="Core.Domain.AppSettings.McpPort"/> aus den Einstellungen (Standard 47123),
    /// <c>0</c> = zufälliger freier Port (für Tests; tatsächlicher Endpoint steht in <see cref="IMcpServerHost.Endpoint"/>).
    /// </summary>
    public int? Port { get; set; }

    /// <summary>HTTP-Server starten (Streamable HTTP auf 127.0.0.1/mcp).</summary>
    public bool EnableHttp { get; set; } = true;

    /// <summary>Named-Pipe-Server für die Claude-Desktop-Brücke mitstarten.</summary>
    public bool EnablePipe { get; set; } = true;

    /// <summary>Name der Named Pipe (Standard <see cref="AppPaths.BridgePipeName"/>; in Tests eindeutig setzen).</summary>
    public string PipeName { get; set; } = AppPaths.BridgePipeName;

    /// <summary>
    /// Brücke ohne Token zulassen: Dann wird der in den Einstellungen angelegte Client mit Namen/ID
    /// <see cref="TokenlessPipeClientName"/> verwendet. Standard <c>false</c> – die Brücke muss ein Token senden.
    /// </summary>
    public bool AllowTokenlessPipeClient { get; set; }

    /// <summary>Client, der bei <see cref="AllowTokenlessPipeClient"/> für tokenlose Pipe-Verbindungen gilt.</summary>
    public string TokenlessPipeClientName { get; set; } = "claude-desktop";

    /// <summary>
    /// Sitzungsmodus des Streamable-HTTP-Transports. Standard <see cref="HttpServerSessionMode.Stateless"/>:
    /// Clients nach Spezifikation 2026-07-28 werden pro Anfrage bedient, ältere Clients (2025-11-25 und früher,
    /// mit <c>initialize</c>-Handshake) ebenfalls – nur ohne <c>Mcp-Session-Id</c> und ohne GET-Stream.
    /// </summary>
    public HttpServerSessionMode SessionMode { get; set; } = HttpServerSessionMode.Stateless;

    /// <summary>
    /// Zusätzlich erlaubte Host-Header (z. B. <c>host.docker.internal:47123</c> für Open WebUI in Docker).
    /// Standard leer: nur <c>127.0.0.1:&lt;port&gt;</c> und <c>localhost:&lt;port&gt;</c>.
    /// </summary>
    public IList<string> AdditionalAllowedHosts { get; } = new List<string>();

    /// <summary>Zusätzlich erlaubte Origins (exakt, z. B. <c>http://localhost:3000</c> ist bereits erlaubt).</summary>
    public IList<string> AdditionalAllowedOrigins { get; } = new List<string>();

    /// <summary>Verzeichnis der verschlüsselten Token-Dateien (Standard <see cref="AppPaths.TokensDirectory"/>).</summary>
    public string? TokensDirectory { get; set; }

    /// <summary>Servername, wie ihn MCP-Clients sehen.</summary>
    public string ServerName { get; set; } = "musterdepot";
}
