using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;

namespace Musterdepot.Mcp;

/// <summary>
/// Token pro KI-Client.
/// <list type="bullet">
/// <item>Token: 32 zufällige Bytes, base64url, Präfix <c>mdp_</c>; wird bei <see cref="CreateClientAsync"/> einmalig im Klartext zurückgegeben.</item>
/// <item>In <see cref="AppSettings.AiClients"/> (über <see cref="ISettingsStore"/>) steht nur der SHA-256-Hash (hex) plus Metadaten.</item>
/// <item>Das Token selbst liegt unter <see cref="AppPaths.TokensDirectory"/>\&lt;clientId&gt;.token – auf Windows mit DPAPI
/// (<see cref="DataProtectionScope.CurrentUser"/>) verschlüsselt. Auf anderen Systemen (nur Entwicklung/Tests) liegt es im
/// <b>Klartext</b> in einer Datei mit Rechten 600 – dieser Entwicklungsmodus ist nicht für echten Betrieb gedacht.</item>
/// <item>Validierung vergleicht Hashes zeitkonstant (<see cref="CryptographicOperations.FixedTimeEquals"/>) über alle Clients.</item>
/// </list>
/// </summary>
public sealed partial class TokenService : ITokenService
{
    public const string TokenPrefix = "mdp_";
    private const string FileExtension = ".token";
    private static readonly byte[] DpapiEntropy = "Musterdepot.Mcp.Token.v1"u8.ToArray();

    private readonly ISettingsStore _settings;
    private readonly IClientRegistry? _registry;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private readonly string _tokensDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TokenService(ISettingsStore settings, IOptions<McpOptions> options, IClientRegistry? registry = null, IClock? clock = null, ILogger<TokenService>? logger = null)
    {
        _settings = settings;
        _registry = registry;
        _clock = clock ?? SystemClock.Instance;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _tokensDirectory = options.Value.TokensDirectory ?? AppPaths.TokensDirectory;
    }

    /// <summary>true, wenn Tokens per DPAPI verschlüsselt werden (Windows); false = Entwicklungsmodus (Klartext, Rechte 600).</summary>
    public static bool UsesDpapi => OperatingSystem.IsWindows();

    public string TokensDirectory => _tokensDirectory;

    public async Task<(AiClientConfig Client, string Token)> CreateClientAsync(string name, ClientScopes scopes, ClientMode mode, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var token = TokenPrefix + Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var settings = await _settings.LoadAsync(ct).ConfigureAwait(false);
            string clientId;
            do clientId = $"{Slug(name)}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3))}";
            while (settings.AiClients.Any(c => string.Equals(c.ClientId, clientId, StringComparison.OrdinalIgnoreCase)));

            var client = new AiClientConfig
            {
                ClientId = clientId,
                Name = name.Trim(),
                Mode = mode,
                Scopes = scopes,
                TokenHash = HashToken(token),
                CreatedUtc = _clock.UtcNow,
                Enabled = true,
            };
            WriteTokenFile(clientId, token);
            await _settings.SaveAsync(settings with { AiClients = [.. settings.AiClients, client] }, ct).ConfigureAwait(false);
            _logger.LogInformation("KI-Client {ClientId} ({Name}) angelegt, Rechte {Scopes}, Modus {Mode}", clientId, client.Name, scopes, mode);
            return (client, token);
        }
        finally { _gate.Release(); }
    }

    public async Task<ClientIdentity?> ValidateAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 256) return null;
        var presented = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        var settings = await _settings.LoadAsync(ct).ConfigureAwait(false);

        AiClientConfig? match = null;
        var stored = new byte[32];
        foreach (var c in settings.AiClients)
        {
            // Jeden Client prüfen (kein frühes Ende), damit die Laufzeit nicht verrät, welcher Eintrag passte.
            bool ok = c.TokenHash is { Length: 64 } h
                      && Convert.FromHexString(h, stored, out _, out var written) == System.Buffers.OperationStatus.Done
                      && written == 32
                      && CryptographicOperations.FixedTimeEquals(presented, stored);
            if (ok && match is null) match = c;
        }
        if (match is null || !match.Enabled) return null;

        // Modus und Rechte bevorzugt aus der Client-Registry (dort ändert die Oberfläche sie), sonst aus den Einstellungen.
        var current = _registry?.Get(match.ClientId) ?? match;
        if (!current.Enabled) return null;
        return new ClientIdentity(current.ClientId, current.Name, current.Mode, current.Scopes);
    }

    public async Task RevokeAsync(string clientId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var settings = await _settings.LoadAsync(ct).ConfigureAwait(false);
            var remaining = settings.AiClients.Where(c => !string.Equals(c.ClientId, clientId, StringComparison.Ordinal)).ToList();
            if (remaining.Count != settings.AiClients.Count)
                await _settings.SaveAsync(settings with { AiClients = remaining }, ct).ConfigureAwait(false);
            if (IsValidClientId(clientId))
            {
                var path = TokenPath(clientId);
                if (File.Exists(path)) File.Delete(path);
            }
            _logger.LogInformation("KI-Client {ClientId} widerrufen", clientId);
        }
        finally { _gate.Release(); }
    }

    public async Task<string?> GetTokenAsync(string clientId, CancellationToken ct = default)
    {
        if (!IsValidClientId(clientId)) return null;
        var path = TokenPath(clientId);
        if (!File.Exists(path)) return null;
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        try
        {
            if (OperatingSystem.IsWindows())
                bytes = ProtectedData.Unprotect(bytes, DpapiEntropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes).Trim();
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(ex, "Token-Datei für {ClientId} konnte nicht entschlüsselt werden", clientId);
            return null;
        }
    }

    /// <summary>SHA-256 des Tokens als Kleinbuchstaben-Hex (so steht es in <see cref="AiClientConfig.TokenHash"/>).</summary>
    public static string HashToken(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private void WriteTokenFile(string clientId, string token)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(_tokensDirectory);
            bytes = ProtectedData.Protect(bytes, DpapiEntropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(TokenPath(clientId), bytes);
            return;
        }

        // Entwicklungsmodus (Linux/macOS): Klartext, Verzeichnis 700, Datei 600.
        Directory.CreateDirectory(_tokensDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = TokenPath(clientId);
        if (File.Exists(path)) File.Delete(path);
        using var fs = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
        fs.Write(bytes);
    }

    private string TokenPath(string clientId) => Path.Combine(_tokensDirectory, clientId + FileExtension);

    private static bool IsValidClientId(string clientId) => !string.IsNullOrEmpty(clientId) && ClientIdRegex().IsMatch(clientId);

    internal static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name.Trim().ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
            if (sb.Length >= 24) break;
        }
        var s = sb.ToString().Trim('-');
        return s.Length == 0 ? "client" : s;
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex ClientIdRegex();
}
