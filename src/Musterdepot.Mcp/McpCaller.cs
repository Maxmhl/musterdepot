using System.Security.Claims;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;

namespace Musterdepot.Mcp;

/// <summary>
/// Überträgt die <see cref="ClientIdentity"/> eines KI-Clients als <see cref="ClaimsPrincipal"/>.
/// HTTP: die Bearer-Middleware setzt <c>HttpContext.User</c>, das SDK reicht ihn als <c>RequestContext.User</c> weiter.
/// Pipe: ein Eingangsfilter setzt <c>MessageContext.User</c> pro Nachricht.
/// </summary>
public static class McpCaller
{
    public const string AuthenticationType = "MusterdepotToken";
    public const string ClientIdClaim = "mdp:client_id";
    public const string NameClaim = "mdp:name";
    public const string ModeClaim = "mdp:mode";
    public const string ScopesClaim = "mdp:scopes";

    public static ClaimsPrincipal ToPrincipal(ClientIdentity identity) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClientIdClaim, identity.ClientId),
                new Claim(NameClaim, identity.Name),
                new Claim(ModeClaim, identity.Mode.ToString()),
                new Claim(ScopesClaim, ((int)identity.Scopes).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ],
            AuthenticationType, NameClaim, null));

    public static ClientIdentity? FromPrincipal(ClaimsPrincipal? principal)
    {
        if (principal?.Identity is not { IsAuthenticated: true } || principal.Identity.AuthenticationType != AuthenticationType)
            return null;
        var id = principal.FindFirst(ClientIdClaim)?.Value;
        if (string.IsNullOrEmpty(id)) return null;
        var name = principal.FindFirst(NameClaim)?.Value ?? id;
        var mode = Enum.TryParse<ClientMode>(principal.FindFirst(ModeClaim)?.Value, out var m) ? m : ClientMode.ReadOnly;
        var scopes = int.TryParse(principal.FindFirst(ScopesClaim)?.Value, out var s) ? (ClientScopes)s : ClientScopes.None;
        return new ClientIdentity(id, name, mode, scopes);
    }
}
