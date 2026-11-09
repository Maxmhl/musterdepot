using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Commands;
using Musterdepot.Core.Domain;
using Musterdepot.Core.ReadModels;

namespace Musterdepot.Mcp.Tests;

/// <summary>Echter lokaler MCP-Server auf zufälligem Port mit Fakes.</summary>
public sealed class McpTestServer : IAsyncDisposable
{
    public FakeCommandBus Bus { get; } = new();
    public FakeQueries Queries { get; } = new();
    public InMemorySettingsStore Settings { get; } = new();
    public InMemoryAuditLog Audit { get; } = new();
    public ServiceProvider Services { get; }
    public McpServerHost Host { get; }
    public TokenService Tokens { get; }
    public string PipeName { get; } = "mdp-test-" + Guid.NewGuid().ToString("N")[..12];
    public string TokensDirectory { get; } = Path.Combine(Path.GetTempPath(), "mdp-tokens-" + Guid.NewGuid().ToString("N"));

    public string FullToken { get; private set; } = "";
    public string FullClientId { get; private set; } = "";
    public string ReadToken { get; private set; } = "";
    public string ReadClientId { get; private set; } = "";
    public Uri Endpoint => Host.Endpoint!;

    private McpTestServer(Action<McpOptions>? configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICommandBus>(Bus);
        services.AddSingleton<ITradingQueries>(Queries);
        services.AddSingleton<ISettingsStore>(Settings);
        services.AddSingleton<IAuditLog>(Audit);
        services.AddMusterdepotMcp(o =>
        {
            o.Port = 0;
            o.PipeName = PipeName;
            o.TokensDirectory = TokensDirectory;
            configure?.Invoke(o);
        });
        Services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        Host = Services.GetRequiredService<McpServerHost>();
        Tokens = (TokenService)Services.GetRequiredService<ITokenService>();
    }

    public static async Task<McpTestServer> StartAsync(Action<McpOptions>? configure = null)
    {
        var server = new McpTestServer(configure);
        var (full, fullToken) = await server.Tokens.CreateClientAsync("Claude Code", ClientScopes.All, ClientMode.Autonomous);
        var (read, readToken) = await server.Tokens.CreateClientAsync("Nur Lesen", ClientScopes.Read, ClientMode.ReadOnly);
        server.FullToken = fullToken;
        server.FullClientId = full.ClientId;
        server.ReadToken = readToken;
        server.ReadClientId = read.ClientId;
        await server.Services.GetRequiredService<IMcpServerHost>().StartAsync();
        return server;
    }

    public Task<McpClient> ConnectAsync(string? token = null, string? protocolVersion = null)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = Endpoint,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token ?? FullToken}" },
        });
        return McpClient.CreateAsync(transport, new McpClientOptions
        {
            ClientInfo = new Implementation { Name = "musterdepot-tests", Version = "1.0" },
            ProtocolVersion = protocolVersion,
        });
    }

    public async ValueTask DisposeAsync()
    {
        await Host.StopAsync();
        await Services.DisposeAsync();
        try { Directory.Delete(TokensDirectory, recursive: true); } catch { }
    }
}

public static class ToolResultExtensions
{
    public static JsonElement Json(this CallToolResult result)
    {
        if (result.StructuredContent is { } s) return s;
        var text = result.Content.OfType<TextContentBlock>().First().Text;
        return JsonDocument.Parse(text).RootElement;
    }

    public static string Status(this CallToolResult result) => result.Json().GetProperty("status").GetString()!;
}
