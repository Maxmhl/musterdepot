using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Musterdepot.Core.Abstractions;

namespace Musterdepot.Mcp.Security;

/// <summary>
/// Schutz gegen DNS-Rebinding und fremde Webseiten: nur Host <c>127.0.0.1:&lt;port&gt;</c> bzw. <c>localhost:&lt;port&gt;</c>,
/// und – falls ein <c>Origin</c>-Header vorhanden ist – nur <c>http(s)://127.0.0.1|localhost[:port]</c>. Sonst 403.
/// </summary>
public sealed class LocalRequestGuard(int port, IEnumerable<string> extraHosts, IEnumerable<string> extraOrigins, ILogger logger)
{
    private readonly HashSet<string> _hosts = new(
        new[] { $"127.0.0.1:{port}", $"localhost:{port}" }.Concat(extraHosts.Select(h => h.Trim())),
        StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _origins = new(extraOrigins.Select(o => o.Trim().TrimEnd('/')), StringComparer.OrdinalIgnoreCase);

    public bool IsHostAllowed(string? host) => !string.IsNullOrEmpty(host) && _hosts.Contains(host);

    public bool IsOriginAllowed(string? origin)
    {
        if (origin is null) return true; // Nicht-Browser-Clients senden kein Origin.
        if (_origins.Contains(origin.TrimEnd('/'))) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false; // auch "null"
        if (uri.Scheme is not ("http" or "https")) return false;
        if (uri.AbsolutePath is not ("/" or "") || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        return uri.Host is "127.0.0.1" or "localhost";
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var host = context.Request.Headers.Host.ToString();
        var origin = context.Request.Headers.Origin.Count > 0 ? context.Request.Headers.Origin.ToString() : null;
        if (!IsHostAllowed(host) || !IsOriginAllowed(origin))
        {
            logger.LogWarning("MCP-Anfrage abgelehnt (403): Host '{Host}', Origin '{Origin}'", host, origin);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("Forbidden: nur lokale Anfragen an 127.0.0.1/localhost sind erlaubt.");
            return;
        }
        await next(context);
    }
}

/// <summary>Bearer-Token-Prüfung: <c>Authorization: Bearer mdp_…</c> → <see cref="ITokenService.ValidateAsync"/>; sonst 401.</summary>
public sealed class BearerTokenAuthenticator(ITokenService tokens, ILogger logger)
{
    public static string? ExtractBearer(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        var h = header.Trim();
        const string scheme = "Bearer ";
        if (h.Length <= scheme.Length || !h.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return null;
        var token = h[scheme.Length..].Trim();
        return token.Length == 0 ? null : token;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var token = ExtractBearer(context.Request.Headers.Authorization.ToString());
        var identity = token is null ? null : await tokens.ValidateAsync(token, context.RequestAborted);
        if (identity is null)
        {
            logger.LogWarning("MCP-Anfrage ohne gültiges Token abgelehnt (401) von {Remote}", context.Connection.RemoteIpAddress);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer realm=\"musterdepot\"";
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync(token is null
                ? "Unauthorized: Header 'Authorization: Bearer <token>' fehlt."
                : "Unauthorized: Token ungültig, widerrufen oder Client deaktiviert.");
            return;
        }
        context.User = McpCaller.ToPrincipal(identity);
        context.Items[typeof(ClientIdentity)] = identity;
        await next(context);
    }
}
