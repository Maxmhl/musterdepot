using Microsoft.Extensions.Options;
using Musterdepot.Core.Domain;
using Musterdepot.Mcp.Security;

namespace Musterdepot.Mcp.Tests;

public sealed class TokenServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mdp-tok-" + Guid.NewGuid().ToString("N"));
    private readonly InMemorySettingsStore _settings = new();
    private readonly TokenService _service;

    public TokenServiceTests() =>
        _service = new TokenService(_settings, Options.Create(new McpOptions { TokensDirectory = _dir }));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public async Task Create_generates_prefixed_random_token_and_stores_only_hash()
    {
        var (client, token) = await _service.CreateClientAsync("Claude Code", ClientScopes.Read | ClientScopes.Trade, ClientMode.Confirm);
        Assert.StartsWith("mdp_", token);
        Assert.Equal(4 + 43, token.Length); // 32 Bytes base64url ohne Padding
        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.StartsWith("claude-code-", client.ClientId);

        var stored = Assert.Single((await _settings.LoadAsync()).AiClients);
        Assert.Equal(TokenService.HashToken(token), stored.TokenHash);
        Assert.Equal(64, stored.TokenHash!.Length);
        Assert.NotEqual(token, stored.TokenHash);
        Assert.Equal(ClientMode.Confirm, stored.Mode);

        var (_, other) = await _service.CreateClientAsync("Claude Code", ClientScopes.Read, ClientMode.ReadOnly);
        Assert.NotEqual(token, other);
    }

    [Fact]
    public async Task Token_file_is_protected_and_readable_again()
    {
        var (client, token) = await _service.CreateClientAsync("LM Studio", ClientScopes.Read, ClientMode.ReadOnly);
        var path = Path.Combine(_dir, client.ClientId + ".token");
        Assert.True(File.Exists(path));
        if (OperatingSystem.IsWindows())
            Assert.NotEqual(token, File.ReadAllText(path)); // DPAPI-verschlüsselt
        else
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Equal(token, await _service.GetTokenAsync(client.ClientId));
        Assert.Null(await _service.GetTokenAsync("../etc/passwd"));
    }

    [Fact]
    public async Task Validate_returns_identity_and_rejects_wrong_tokens()
    {
        var (client, token) = await _service.CreateClientAsync("Claude Code", ClientScopes.All, ClientMode.Autonomous);
        await _service.CreateClientAsync("Zweiter", ClientScopes.Read, ClientMode.ReadOnly);

        var id = await _service.ValidateAsync(token);
        Assert.NotNull(id);
        Assert.Equal(client.ClientId, id!.ClientId);
        Assert.Equal("Claude Code", id.Name);
        Assert.Equal(ClientScopes.All, id.Scopes);
        Assert.Equal(ClientMode.Autonomous, id.Mode);

        Assert.Null(await _service.ValidateAsync(""));
        Assert.Null(await _service.ValidateAsync(token + "x"));
        Assert.Null(await _service.ValidateAsync("mdp_" + new string('A', 43)));
    }

    [Fact]
    public async Task Disabled_client_is_rejected()
    {
        var (client, token) = await _service.CreateClientAsync("Claude Code", ClientScopes.All, ClientMode.Autonomous);
        var s = await _settings.LoadAsync();
        await _settings.SaveAsync(s with { AiClients = s.AiClients.Select(c => c with { Enabled = false }).ToList() });
        Assert.Null(await _service.ValidateAsync(token));
    }

    [Fact]
    public async Task Revoke_removes_client_and_token_file()
    {
        var (client, token) = await _service.CreateClientAsync("Claude Code", ClientScopes.All, ClientMode.Autonomous);
        await _service.RevokeAsync(client.ClientId);
        Assert.Null(await _service.ValidateAsync(token));
        Assert.Empty((await _settings.LoadAsync()).AiClients);
        Assert.Null(await _service.GetTokenAsync(client.ClientId));
        Assert.False(File.Exists(Path.Combine(_dir, client.ClientId + ".token")));
    }

    [Theory]
    [InlineData("Bearer abc", "abc")]
    [InlineData("bearer  abc ", "abc")]
    [InlineData("Basic abc", null)]
    [InlineData("Bearer", null)]
    [InlineData("", null)]
    public void Bearer_header_parsing(string header, string? expected) =>
        Assert.Equal(expected, BearerTokenAuthenticator.ExtractBearer(header));
}
