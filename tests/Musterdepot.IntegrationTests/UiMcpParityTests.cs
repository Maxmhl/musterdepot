using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Commands;
using Musterdepot.Core.Domain;
using Musterdepot.IntegrationTests.Infrastructure;
using Musterdepot.Mcp;
using Musterdepot.Recorder.Fixtures;

namespace Musterdepot.IntegrationTests;

/// <summary>
/// Roadmap Phase 4: Oberfläche und MCP liefern gleiche Ergebnisse. Dasselbe Orderskript läuft einmal über
/// <see cref="ICommandBus"/> (Source=Human, Client "ui") und einmal über die MCP-Tools des echten
/// <see cref="IMcpServerHost"/> (SDK-Client, Bearer-Token, Modus autonomous). Verglichen werden Ergebnisstatus,
/// Orders, Fills und Portfolio – ohne IDs, Source und Client-Namen.
/// </summary>
[Collection(TestHostCollection.Name)]
public class UiMcpParityTests
{
    private const string Acc = "main";

    public static TheoryData<string> Names() => ReplayFixture.Names();

    private sealed record Outcome(IReadOnlyList<string> Statuses, string State, int Fills, PortfolioSnapshot Portfolio, JsonElement? McpPortfolio);

    [Theory, MemberData(nameof(Names))]
    public async Task SameScript_GivesSameResults_ViaUiAndMcp(string name)
    {
        var fixture = ReplayFixture.Load(name);
        var ui = await RunAsync(fixture, viaMcp: false);
        var mcp = await RunAsync(fixture, viaMcp: true);

        Assert.Equal(ui.Statuses, mcp.Statuses);
        Assert.Contains(ui.Statuses, s => s.EndsWith(": executed", StringComparison.Ordinal));
        Assert.True(ui.Fills > 0, "Szenario ohne Fills ist für den Vergleich wertlos.");
        if (ui.State != mcp.State)
        {
            var diff = GoldenFile.Normalize(ui.State).Split('\n').Zip(GoldenFile.Normalize(mcp.State).Split('\n'))
                .Select((p, i) => (p, i)).First(x => x.p.First != x.p.Second);
            Assert.Fail($"Zustand weicht ab ab Zeile {diff.i + 1}:\n  Oberfläche: {diff.p.First.Trim()}\n  MCP:        {diff.p.Second.Trim()}");
        }

        // Das MCP-Lesetool get_portfolio liefert dieselben Zahlen wie die Engine.
        var pf = mcp.McpPortfolio!.Value;
        Assert.Equal(mcp.Portfolio.Cash, pf.GetProperty("cash").GetDecimal());
        Assert.Equal(mcp.Portfolio.Positions.Count, pf.GetProperty("positions").GetArrayLength());
    }

    private static async Task<Outcome> RunAsync(ReplayFixture fixture, bool viaMcp)
    {
        await using var host = TestHost.Create(o =>
        {
            o.ReplayFile = fixture.ReplayPath;
            o.StartUtc = fixture.StartUtc;
            // Kein Hintergrund-Takt: OnClockTick nur an festen Stellen, damit beide Läufe deterministisch sind.
            o.Commands.ClockTickInterval = TimeSpan.FromHours(1);
        });
        var instruments = host.Get<IInstrumentStore>();
        await instruments.UpsertAsync(fixture.Script.Instruments);
        await host.Runtime.StartAsync();
        var replay = host.Replay!;
        var engine = host.Engine;

        McpClient? client = null;
        if (viaMcp)
        {
            var (_, token) = await host.Get<ITokenService>().CreateClientAsync("Paritaet-KI", ClientScopes.Read | ClientScopes.Trade, ClientMode.Autonomous);
            var mcpHost = host.Get<IMcpServerHost>();
            await mcpHost.StartAsync();
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = mcpHost.Endpoint!,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
            });
            client = await McpClient.CreateAsync(transport, new McpClientOptions { ClientInfo = new Implementation { Name = "paritaet", Version = "1.0" } });
        }

        try
        {
            var orderIds = new Dictionary<string, string>();
            var statuses = new List<string>();
            foreach (var step in fixture.Script.Steps)
            {
                replay.AdvanceTo(step.AtUtc);
                engine.OnClockTick(host.Clock.UtcNow);
                string status;
                if (step.PlaceOrder is { } p)
                {
                    string? orderId;
                    (status, orderId) = viaMcp ? await PlaceViaMcpAsync(client!, p) : await PlaceViaUiAsync(host.Bus, p);
                    if (orderId is not null) orderIds[p.ClientOrderId] = orderId;
                }
                else if (step.Cancel is { } c)
                {
                    var orderId = orderIds.GetValueOrDefault(c.ClientOrderId!) ?? "unbekannt";
                    status = viaMcp
                        ? Status(await client!.CallToolAsync("cancel_order", new Dictionary<string, object?> { ["order_id"] = orderId, ["idempotency_key"] = "cancel-" + c.ClientOrderId }))
                        : McpJson.Status((await host.Bus.SendAsync(new CancelOrderCommand { IdempotencyKey = "cancel-" + c.ClientOrderId, OrderId = orderId })).Status);
                }
                else throw new NotSupportedException(step.Action.ToString());
                statuses.Add($"{GoldenReplay.TargetClientOrderId(step)}: {status}");
            }
            replay.RunToEnd();
            engine.OnClockTick(host.Clock.UtcNow);
            Invariants.CheckAccount(engine, Acc);

            JsonElement? mcpPortfolio = client is null ? null
                : Data(await client.CallToolAsync("get_portfolio", new Dictionary<string, object?>()));
            var portfolio = engine.GetPortfolio(Acc);
            await host.Runtime.StopAsync();
            return new Outcome(statuses, Normalize(engine), engine.GetFills(Acc).Count, portfolio, mcpPortfolio);
        }
        finally
        {
            if (client is not null) await client.DisposeAsync();
        }
    }

    private static async Task<(string Status, string? OrderId)> PlaceViaUiAsync(ICommandBus bus, OrderScriptPlaceOrder p)
    {
        var r = await bus.SendAsync(p.ToCommand(Acc, CommandSource.Human, "ui"));
        return (McpJson.Status(r.Status), (r.Data as Order)?.OrderId);
    }

    private static async Task<(string Status, string? OrderId)> PlaceViaMcpAsync(McpClient client, OrderScriptPlaceOrder p)
    {
        static string Snake(Enum e) => JsonNamingPolicy.SnakeCaseLower.ConvertName(e.ToString());
        var args = new Dictionary<string, object?>
        {
            ["symbol"] = p.Symbol,
            ["side"] = Snake(p.Side),
            ["order_type"] = Snake(p.Type),
            ["idempotency_key"] = p.ClientOrderId,
            ["time_in_force"] = Snake(p.TimeInForce),
        };
        if (p.Quantity is { } q) args["quantity"] = q;
        if (p.Notional is { } n) args["notional"] = n;
        if (p.LimitPrice is { } l) args["limit_price"] = l;
        if (p.StopPrice is { } s) args["stop_price"] = s;
        if (p.StrategyTag is { } tag) args["strategy_tag"] = tag;
        var result = await client.CallToolAsync("place_order", args);
        var envelope = Envelope(result);
        string? orderId = envelope.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("order_id", out var id) ? id.GetString() : null;
        return (envelope.GetProperty("status").GetString()!, orderId);
    }

    private static JsonElement Envelope(CallToolResult result) =>
        result.StructuredContent is { } s ? s : JsonDocument.Parse(result.Content.OfType<TextContentBlock>().First().Text).RootElement;

    private static string Status(CallToolResult result) => Envelope(result).GetProperty("status").GetString()!;

    private static JsonElement Data(CallToolResult result)
    {
        var e = Envelope(result);
        Assert.Equal("ok", e.GetProperty("status").GetString());
        return e.GetProperty("data");
    }

    private static readonly HashSet<string> IgnoredKeys = new(StringComparer.Ordinal)
    {
        "orderId", "clientOrderId", "source", "clientName", "fillId", "parentOrderId", "lotId", "openingFillId", "entryId", "reference",
    };

    /// <summary>Orders, Fills, Buchungen und Portfolio ohne IDs, Source und Client-Namen.</summary>
    private static string Normalize(ITradingEngine engine)
    {
        var p = engine.GetPortfolio(Acc);
        var state = new
        {
            Orders = engine.GetOrders(Acc).OrderBy(o => o.CreatedUtc).ThenBy(o => o.OrderId, StringComparer.Ordinal),
            Fills = engine.GetFills(Acc),
            CashEntries = engine.GetCashEntries(Acc),
            p.Cash, p.ReservedCash, p.PositionsValue, p.RealizedPnl, p.UnrealizedPnl, p.FeesPaid,
            p.Positions,
        };
        var node = JsonSerializer.SerializeToNode(state, GoldenFile.JsonOptions)!;
        Strip(node);
        return node.ToJsonString(GoldenFile.JsonOptions);
    }

    private static void Strip(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(kv => kv.Key).Where(IgnoredKeys.Contains).ToList()) o.Remove(key);
                // Storno-Grund nennt den Auslöser ("user" bzw. "ai:<Client>") – ebenfalls Identität.
                if (o["status"]?.GetValue<string>() == "Cancelled" && o["rejectReason"] is JsonValue r
                    && r.GetValue<string>() is "user" or ['a', 'i', ':', ..])
                    o["rejectReason"] = "<Auslöser>";
                foreach (var kv in o) Strip(kv.Value);
                break;
            case JsonArray a:
                foreach (var item in a) Strip(item);
                break;
        }
    }

    [Fact]
    public async Task KillSwitch_ViaMcp_OnlyHumanCanRelease()
    {
        var fixture = ReplayFixture.Load(FixtureGenerator.QuietName);
        await using var host = TestHost.Create(o => { o.ReplayFile = fixture.ReplayPath; o.StartUtc = fixture.StartUtc; });
        await host.Runtime.StartAsync();
        host.Replay!.AdvanceTo(fixture.StartUtc.AddMinutes(3));
        var (_, token) = await host.Get<ITokenService>().CreateClientAsync("Not-Aus-KI", ClientScopes.Read | ClientScopes.Trade, ClientMode.Autonomous);
        var mcpHost = host.Get<IMcpServerHost>();
        await mcpHost.StartAsync();
        await using var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = mcpHost.Endpoint!,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
        }));

        // Es gibt kein Tool zum Lösen.
        var tools = (await client.ListToolsAsync()).Select(t => t.Name).ToList();
        Assert.Contains("engage_kill_switch", tools);
        Assert.DoesNotContain(tools, t => t.Contains("release", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("executed", Status(await client.CallToolAsync("engage_kill_switch", new Dictionary<string, object?> { ["reason"] = "Test über MCP" })));
        Assert.True(host.Engine.IsKillSwitchEngaged(Acc));
        var blocked = await client.CallToolAsync("place_order", new Dictionary<string, object?>
        {
            ["symbol"] = "BTC/EUR", ["side"] = "buy", ["order_type"] = "market", ["quantity"] = 0.01m, ["idempotency_key"] = "nach-not-aus",
        });
        Assert.Equal("rejected", Status(blocked));
        Assert.True(blocked.IsError);
        var status = Data(await client.CallToolAsync("get_agent_status", new Dictionary<string, object?>()));
        Assert.True(status.GetProperty("kill_switch_engaged").GetBoolean());

        // Der Mensch löst im Programm.
        Assert.Equal(CommandStatus.Executed, (await host.Bus.SendAsync(new ReleaseKillSwitchCommand { IdempotencyKey = "release" })).Status);
        Assert.False(host.Engine.IsKillSwitchEngaged(Acc));
        Assert.Equal("executed", Status(await client.CallToolAsync("place_order", new Dictionary<string, object?>
        {
            ["symbol"] = "BTC/EUR", ["side"] = "buy", ["order_type"] = "market", ["quantity"] = 0.01m, ["idempotency_key"] = "nach-freigabe",
        })));
        await host.Runtime.StopAsync();
    }

    [Fact]
    public void Normalization_IgnoresOnlyIdentityFields()
    {
        var o = JsonNode.Parse("""{"orderId":"a","price":1.5,"lots":[{"lotId":"x","quantity":2}],"source":"Ai"}""");
        Strip(o);
        Assert.Equal("""{"price":1.5,"lots":[{"quantity":2}]}""", o!.ToJsonString());
        var ui = JsonNode.Parse("""{"status":"Cancelled","rejectReason":"user"}""");
        var ai = JsonNode.Parse("""{"status":"Cancelled","rejectReason":"ai:Claude"}""");
        var other = JsonNode.Parse("""{"status":"Cancelled","rejectReason":"Tagesende"}""");
        Strip(ui); Strip(ai); Strip(other);
        Assert.Equal(ui!.ToJsonString(), ai!.ToJsonString());
        Assert.Contains("Tagesende", other!.ToJsonString());
    }
}
