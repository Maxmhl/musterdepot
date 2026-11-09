using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Musterdepot.Core.Commands;
using Musterdepot.Core.Domain;

namespace Musterdepot.Mcp.Tests;

public sealed class PipeBridgeTests : IAsyncLifetime
{
    private McpTestServer _server = null!;

    public async Task InitializeAsync() => _server = await McpTestServer.StartAsync();
    public async Task DisposeAsync() => await _server.DisposeAsync();

    private async Task<(NamedPipeClientStream Pipe, JsonElement Reply)> ConnectAsync(string handshakeJson)
    {
        var pipe = new NamedPipeClientStream(".", _server.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(5000);
        await pipe.WriteAsync(Encoding.UTF8.GetBytes(handshakeJson + "\n"));
        await pipe.FlushAsync();
        var line = await Bridge.BridgePipeServer.ReadLineAsync(pipe, 8192, CancellationToken.None);
        Assert.NotNull(line);
        return (pipe, JsonDocument.Parse(line!).RootElement.Clone());
    }

    [Fact]
    public async Task Pipe_round_trip_with_token()
    {
        var (pipe, reply) = await ConnectAsync(JsonSerializer.Serialize(new { type = "musterdepot-bridge", version = 1, token = _server.FullToken }));
        await using var _ = pipe;
        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.Equal(_server.FullClientId, reply.GetProperty("client_id").GetString());

        await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe, null), new McpClientOptions
        {
            ClientInfo = new Implementation { Name = "pipe-test", Version = "1" },
        });
        var tools = await client.ListToolsAsync();
        Assert.Equal(HttpServerTests.ExpectedTools.Order(), tools.Select(t => t.Name).Order());

        var quote = await client.CallToolAsync("get_quote", new Dictionary<string, object?> { ["symbol"] = "BTC/EUR" });
        Assert.Equal("kraken", quote.Json().GetProperty("data").GetProperty("source").GetString());

        var order = await client.CallToolAsync("place_order", new Dictionary<string, object?>
        {
            ["symbol"] = "BTC/EUR", ["side"] = "sell", ["order_type"] = "limit", ["quantity"] = 0.5m, ["limit_price"] = 70_000m, ["idempotency_key"] = "pipe-1",
        });
        Assert.Equal("executed", order.Status());
        var cmd = Assert.IsType<PlaceOrderCommand>(Assert.Single(_server.Bus.Received));
        Assert.Equal(CommandSource.Ai, cmd.Source);
        Assert.Equal(_server.FullClientId, cmd.ClientId);
        Assert.Contains(_server.Audit.Entries, e => e.Action == "get_quote" && e.ClientId == _server.FullClientId);
    }

    [Fact]
    public async Task Pipe_round_trip_with_older_protocol()
    {
        var (pipe, reply) = await ConnectAsync(JsonSerializer.Serialize(new { token = _server.ReadToken }));
        await using var _ = pipe;
        Assert.True(reply.GetProperty("ok").GetBoolean());
        await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe, null), new McpClientOptions { ProtocolVersion = "2025-11-25" });
        Assert.Equal("2025-11-25", client.NegotiatedProtocolVersion);
        var denied = await client.CallToolAsync("place_order", new Dictionary<string, object?>
        {
            ["symbol"] = "BTC/EUR", ["side"] = "buy", ["order_type"] = "market", ["quantity"] = 1m, ["idempotency_key"] = "x",
        });
        Assert.Equal("denied", denied.Status());
        Assert.Empty(_server.Bus.Received);
    }

    [Fact]
    public async Task Pipe_rejects_missing_or_invalid_token()
    {
        var (p1, r1) = await ConnectAsync("""{"type":"musterdepot-bridge","version":1}""");
        await using (p1) Assert.False(r1.GetProperty("ok").GetBoolean());

        var (p2, r2) = await ConnectAsync("""{"token":"mdp_falsch"}""");
        await using (p2) Assert.False(r2.GetProperty("ok").GetBoolean());

        var (p3, r3) = await ConnectAsync("kein json");
        await using (p3) Assert.False(r3.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Revocation_takes_effect_on_open_pipe()
    {
        var (pipe, reply) = await ConnectAsync(JsonSerializer.Serialize(new { token = _server.FullToken }));
        await using var _ = pipe;
        Assert.True(reply.GetProperty("ok").GetBoolean());
        await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe, null), new McpClientOptions());
        Assert.Equal("ok", (await client.CallToolAsync("get_portfolio")).Status());

        await _server.Tokens.RevokeAsync(_server.FullClientId);
        var after = await client.CallToolAsync("get_portfolio");
        Assert.True(after.IsError);
        Assert.Equal("denied", after.Status());
    }
}

public sealed class TokenlessPipeTests
{
    [Fact]
    public async Task Tokenless_pipe_uses_claude_desktop_client_when_enabled()
    {
        await using var server = await McpTestServer.StartAsync(o => o.AllowTokenlessPipeClient = true);
        await server.Tokens.CreateClientAsync("claude-desktop", ClientScopes.Read, ClientMode.ReadOnly);

        await using var pipe = new NamedPipeClientStream(".", server.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        await pipe.WriteAsync("{}\n"u8.ToArray());
        var line = await Bridge.BridgePipeServer.ReadLineAsync(pipe, 8192, CancellationToken.None);
        var reply = JsonDocument.Parse(line!).RootElement;
        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.Equal("claude-desktop", reply.GetProperty("name").GetString());
    }
}
