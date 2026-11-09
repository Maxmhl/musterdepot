using System.IO.Pipelines;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Musterdepot.McpBridge;

namespace Musterdepot.Mcp.Tests;

public sealed class BridgeExeTests
{
    [Fact]
    public async Task Bridge_forwards_stdio_to_pipe_byte_exact()
    {
        await using var server = await McpTestServer.StartAsync();
        var toBridge = new Pipe();
        var fromBridge = new Pipe();
        var stderr = new StringWriter();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var run = BridgeRunner.RunAsync(
            new BridgeRunner.Options(server.FullToken, server.PipeName, TimeSpan.FromSeconds(5)),
            toBridge.Reader.AsStream(), fromBridge.Writer.AsStream(), stderr, cts.Token);

        await using (var client = await McpClient.CreateAsync(
                         new StreamClientTransport(toBridge.Writer.AsStream(), fromBridge.Reader.AsStream(), null),
                         new McpClientOptions { ClientInfo = new Implementation { Name = "bridge-test", Version = "1" } }, null, cts.Token))
        {
            var quote = await client.CallToolAsync("get_quote", new Dictionary<string, object?> { ["symbol"] = "BTC/EUR" }, cancellationToken: cts.Token);
            Assert.Equal("ok", quote.Status());
            Assert.Equal(25, (await client.ListToolsAsync(cancellationToken: cts.Token)).Count);
        }

        await toBridge.Writer.CompleteAsync(); // Claude Desktop beendet → stdin zu
        Assert.Equal(BridgeRunner.ExitOk, await run.WaitAsync(cts.Token));
        Assert.Contains("verbunden als Claude Code", stderr.ToString());
    }

    [Fact]
    public async Task Bridge_reports_not_running_on_stderr()
    {
        var stderr = new StringWriter();
        var code = await BridgeRunner.RunAsync(
            new BridgeRunner.Options("mdp_x", "mdp-gibt-es-nicht-" + Guid.NewGuid().ToString("N"), TimeSpan.FromMilliseconds(300)),
            Stream.Null, Stream.Null, stderr);
        Assert.Equal(BridgeRunner.ExitNotRunning, code);
        Assert.Contains("Läuft Musterdepot", stderr.ToString());
    }

    [Fact]
    public async Task Bridge_reports_rejected_token()
    {
        await using var server = await McpTestServer.StartAsync();
        var stderr = new StringWriter();
        var code = await BridgeRunner.RunAsync(new BridgeRunner.Options("mdp_falsch", server.PipeName, TimeSpan.FromSeconds(5)), Stream.Null, Stream.Null, stderr);
        Assert.Equal(BridgeRunner.ExitHandshakeFailed, code);
        Assert.Contains("abgelehnt", stderr.ToString());
    }

    [Fact]
    public void Arguments_and_environment()
    {
        var env = new Dictionary<string, string?> { ["MUSTERDEPOT_TOKEN"] = "mdp_env" };
        var o = BridgeRunner.ParseArgs([], k => env.GetValueOrDefault(k));
        Assert.Equal("mdp_env", o.Token);
        Assert.Equal(Core.Abstractions.AppPaths.BridgePipeName, o.PipeName);

        var o2 = BridgeRunner.ParseArgs(["--token", "mdp_arg", "--pipe", "p1"], k => env.GetValueOrDefault(k));
        Assert.Equal("mdp_arg", o2.Token);
        Assert.Equal("p1", o2.PipeName);
        Assert.Throws<ArgumentException>(() => BridgeRunner.ParseArgs(["--unbekannt"], _ => null));
    }
}
