using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ModelContextProtocol;
using Musterdepot.Core.Commands;
using Musterdepot.Core.Domain;

namespace Musterdepot.Mcp.Tests;

public sealed class HttpServerTests : IAsyncLifetime
{
    private McpTestServer _server = null!;

    public static readonly string[] ExpectedTools =
    [
        "search_symbol", "get_quote", "get_candles", "get_orderbook",
        "place_order", "modify_order", "cancel_order", "list_orders",
        "get_portfolio", "get_positions", "get_trade_history", "get_performance_stats",
        "list_watchlists", "list_alerts", "create_watchlist", "add_to_watchlist", "remove_from_watchlist", "delete_watchlist", "create_alert", "delete_alert",
        "get_settings", "update_settings", "get_audit_log", "get_agent_status", "engage_kill_switch",
    ];

    public async Task InitializeAsync() => _server = await McpTestServer.StartAsync();
    public async Task DisposeAsync() => await _server.DisposeAsync();

    private static Dictionary<string, object?> Args(params (string Key, object? Value)[] items) => items.ToDictionary(i => i.Key, i => i.Value);

    [Fact]
    public void Binds_only_to_loopback_with_random_port()
    {
        Assert.Equal("127.0.0.1", _server.Endpoint.Host);
        Assert.Equal("/mcp", _server.Endpoint.AbsolutePath);
        Assert.NotEqual(0, _server.Endpoint.Port);
        Assert.True(_server.Host.IsRunning);
    }

    [Fact]
    public async Task Tool_list_is_complete_and_annotated()
    {
        await using var client = await _server.ConnectAsync();
        var tools = await client.ListToolsAsync();
        Assert.Equal(ExpectedTools.Order(), tools.Select(t => t.Name).Order());

        var byName = tools.ToDictionary(t => t.Name, t => t.ProtocolTool);
        Assert.True(byName["get_quote"].Annotations?.ReadOnlyHint);
        Assert.False(byName["place_order"].Annotations?.ReadOnlyHint ?? true);
        Assert.True(byName["delete_watchlist"].Annotations?.DestructiveHint);
        Assert.True(byName["engage_kill_switch"].Annotations?.DestructiveHint);
        Assert.All(byName.Values, t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));

        // snake_case-Parameter, idempotency_key ist Pflicht.
        var schema = byName["place_order"].InputSchema;
        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("idempotency_key", required);
        Assert.True(schema.GetProperty("properties").TryGetProperty("dry_run", out _));
        Assert.True(schema.GetProperty("properties").TryGetProperty("take_profit", out _));
        Assert.False(schema.GetProperty("properties").TryGetProperty("context", out _));
    }

    [Fact]
    public async Task Older_protocol_clients_are_served_statelessly()
    {
        await using var client = await _server.ConnectAsync(protocolVersion: "2025-11-25");
        Assert.Equal("2025-11-25", client.NegotiatedProtocolVersion);
        var result = await client.CallToolAsync("get_quote", Args(("symbol", "BTC/EUR")));
        Assert.Equal("ok", result.Status());
    }

    [Fact]
    public async Task Get_quote_contains_provenance_fields()
    {
        await using var client = await _server.ConnectAsync();
        var result = await client.CallToolAsync("get_quote", Args(("symbol", "DE0007164600")));
        Assert.NotEqual(true, result.IsError);
        var data = result.Json().GetProperty("data");
        Assert.Equal("gettex", data.GetProperty("source").GetString());
        Assert.Equal(15, data.GetProperty("delayed_minutes").GetInt32());
        Assert.False(data.GetProperty("is_realtime").GetBoolean());
        Assert.Equal(FakeQueries.Now.AddMinutes(-15), data.GetProperty("as_of_utc").GetDateTimeOffset());
        Assert.Equal(210.5m, data.GetProperty("last").GetDecimal());

        var missing = await client.CallToolAsync("get_quote", Args(("symbol", "XYZ")));
        Assert.Equal("not_found", missing.Status());
    }

    [Fact]
    public async Task Candles_and_orderbook()
    {
        await using var client = await _server.ConnectAsync();
        var candles = (await client.CallToolAsync("get_candles", Args(("symbol", "BTC/EUR"), ("timeframe", "5m"), ("limit", 3)))).Json().GetProperty("data");
        Assert.Equal(3, candles.GetProperty("count").GetInt32());
        Assert.Equal("kraken", candles.GetProperty("source").GetString());
        Assert.True(candles.GetProperty("is_realtime").GetBoolean());

        var book = await client.CallToolAsync("get_orderbook", Args(("symbol", "BTC/EUR")));
        Assert.Equal(59_990m, book.Json().GetProperty("data").GetProperty("best_bid").GetDecimal());

        var stockBook = await client.CallToolAsync("get_orderbook", Args(("symbol", "DE0007164600")));
        Assert.Equal("not_found", stockBook.Status());
    }

    [Fact]
    public async Task Place_order_builds_ai_command_and_calls_bus()
    {
        await using var client = await _server.ConnectAsync();
        var result = await client.CallToolAsync("place_order", Args(
            ("symbol", "BTC/EUR"), ("side", "buy"), ("order_type", "stop_limit"), ("quantity", 0.01m),
            ("limit_price", 61_000m), ("stop_price", 60_500m), ("time_in_force", "gtc"), ("strategy_tag", "breakout"),
            ("take_profit", 65_000m), ("stop_loss", 58_000m), ("oco_group", "g1"), ("idempotency_key", "key-123")));

        Assert.NotEqual(true, result.IsError);
        var json = result.Json();
        Assert.Equal("executed", json.GetProperty("status").GetString());
        Assert.Equal("key-123", json.GetProperty("idempotency_key").GetString());
        Assert.Equal("ord_1", json.GetProperty("data").GetProperty("order_id").GetString());

        var cmd = Assert.IsType<PlaceOrderCommand>(Assert.Single(_server.Bus.Received));
        Assert.Equal(CommandSource.Ai, cmd.Source);
        Assert.Equal(_server.FullClientId, cmd.ClientId);
        Assert.Equal("key-123", cmd.IdempotencyKey);
        Assert.Equal(OrderType.StopLimit, cmd.Type);
        Assert.Equal(OrderSide.Buy, cmd.Side);
        Assert.Equal(TimeInForce.Gtc, cmd.TimeInForce);
        Assert.Equal(0.01m, cmd.Quantity);
        Assert.Equal(61_000m, cmd.LimitPrice);
        Assert.Equal(60_500m, cmd.StopPrice);
        Assert.Equal(65_000m, cmd.TakeProfitPrice);
        Assert.Equal(58_000m, cmd.StopLossPrice);
        Assert.Equal("g1", cmd.OcoGroupId);
        Assert.Equal("breakout", cmd.StrategyTag);
        Assert.False(cmd.DryRun);

        // Schreibbefehle protokolliert die Befehlsschicht – das MCP-Tool schreibt keinen eigenen Eintrag.
        Assert.DoesNotContain(_server.Audit.Entries, e => e.Action == "place_order");
    }

    [Fact]
    public async Task Place_order_without_idempotency_key_is_refused()
    {
        await using var client = await _server.ConnectAsync();
        CallToolResultOrError outcome;
        try
        {
            var r = await client.CallToolAsync("place_order", Args(("symbol", "BTC/EUR"), ("side", "buy"), ("order_type", "market"), ("quantity", 1m)));
            outcome = new(r.IsError == true, null);
        }
        catch (McpException ex) { outcome = new(true, ex); }
        Assert.True(outcome.IsError);

        var empty = await client.CallToolAsync("place_order", Args(("symbol", "BTC/EUR"), ("side", "buy"), ("order_type", "market"), ("quantity", 1m), ("idempotency_key", " ")));
        Assert.True(empty.IsError);
        Assert.Equal("invalid_arguments", empty.Status());
        Assert.Empty(_server.Bus.Received);
    }

    private sealed record CallToolResultOrError(bool IsError, Exception? Exception);

    [Fact]
    public async Task Place_order_validates_quantity_or_notional()
    {
        await using var client = await _server.ConnectAsync();
        var r = await client.CallToolAsync("place_order", Args(("symbol", "BTC/EUR"), ("side", "buy"), ("order_type", "market"), ("idempotency_key", "k")));
        Assert.Equal("invalid_arguments", r.Status());
        var bad = await client.CallToolAsync("place_order", Args(("symbol", "BTC/EUR"), ("side", "hold"), ("order_type", "market"), ("notional", 100m), ("idempotency_key", "k")));
        Assert.Equal("invalid_arguments", bad.Status());
        Assert.Empty(_server.Bus.Received);
    }

    [Fact]
    public async Task Dry_run_returns_preview_and_works_with_read_token()
    {
        await using var client = await _server.ConnectAsync(_server.ReadToken);
        var result = await client.CallToolAsync("place_order", Args(
            ("symbol", "BTC/EUR"), ("side", "buy"), ("order_type", "market"), ("notional", 600m), ("dry_run", true), ("idempotency_key", "preview-1")));
        var json = result.Json();
        Assert.Equal("previewed", json.GetProperty("status").GetString());
        Assert.True(json.GetProperty("data").GetProperty("would_be_accepted").GetBoolean());
        Assert.Equal(2.4m, json.GetProperty("data").GetProperty("estimated_fee").GetDecimal());
        var cmd = Assert.IsType<PlaceOrderCommand>(Assert.Single(_server.Bus.Received));
        Assert.True(cmd.DryRun);
        Assert.Equal(600m, cmd.Notional);
        Assert.Equal(_server.ReadClientId, cmd.ClientId);
    }

    [Fact]
    public async Task Read_token_cannot_trade()
    {
        await using var client = await _server.ConnectAsync(_server.ReadToken);
        var result = await client.CallToolAsync("place_order", Args(
            ("symbol", "BTC/EUR"), ("side", "buy"), ("order_type", "market"), ("quantity", 1m), ("idempotency_key", "k1")));
        Assert.True(result.IsError);
        Assert.Equal("denied", result.Status());
        Assert.Contains("trade", result.Json().GetProperty("message").GetString());
        Assert.Empty(_server.Bus.Received);
        Assert.Contains(_server.Audit.Entries, e => e.Action == "place_order" && e.Result == "denied" && e.ClientId == _server.ReadClientId);

        var settings = await client.CallToolAsync("update_settings", Args(("theme", "dark")));
        Assert.Equal("denied", settings.Status());

        // Lesen und Not-Aus sind erlaubt.
        Assert.Equal("ok", (await client.CallToolAsync("get_portfolio")).Status());
        Assert.Equal("executed", (await client.CallToolAsync("engage_kill_switch", Args(("reason", "test")))).Status());
    }

    [Fact]
    public async Task Pending_approval_tells_ai_that_a_human_must_approve()
    {
        _server.Bus.Handler = _ => new CommandResult { Status = CommandStatus.PendingApproval, ApprovalId = "apr_1", Message = "Wartet auf Freigabe" };
        await using var client = await _server.ConnectAsync();
        var json = (await client.CallToolAsync("cancel_order", Args(("order_id", "ord_9")))).Json();
        Assert.Equal("pending_approval", json.GetProperty("status").GetString());
        Assert.True(json.GetProperty("requires_human_approval").GetBoolean());
        Assert.Equal("apr_1", json.GetProperty("approval_id").GetString());
        Assert.Contains("Mensch", json.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Engage_kill_switch_sends_command()
    {
        await using var client = await _server.ConnectAsync();
        var result = await client.CallToolAsync("engage_kill_switch", Args(("reason", "Datenfehler"), ("idempotency_key", "ks-1")));
        Assert.Equal("executed", result.Status());
        var cmd = Assert.IsType<EngageKillSwitchCommand>(Assert.Single(_server.Bus.Received));
        Assert.Equal("Datenfehler", cmd.Reason);
        Assert.Equal(CommandSource.Ai, cmd.Source);
        Assert.Equal("ks-1", cmd.IdempotencyKey);
    }

    [Fact]
    public async Task Write_tools_map_to_commands()
    {
        await using var client = await _server.ConnectAsync();
        await client.CallToolAsync("modify_order", Args(("order_id", "o1"), ("new_limit_price", 100m)));
        await client.CallToolAsync("create_watchlist", Args(("name", "Tech"), ("symbols", new[] { "DE0007164600", "DE0007164600" })));
        await client.CallToolAsync("add_to_watchlist", Args(("watchlist_id", "wl_1"), ("symbol", "ETH/EUR")));
        await client.CallToolAsync("remove_from_watchlist", Args(("watchlist_id", "wl_1"), ("symbol", "ETH/EUR")));
        await client.CallToolAsync("delete_watchlist", Args(("watchlist_id", "wl_1")));
        await client.CallToolAsync("create_alert", Args(("symbol", "BTC/EUR"), ("condition", "price_above"), ("threshold", 70_000m)));
        await client.CallToolAsync("delete_alert", Args(("alert_id", "al_1")));
        await client.CallToolAsync("update_settings", Args(("theme", "dark"), ("kraken_enabled", false)));

        var cmds = _server.Bus.Received.ToList();
        Assert.Collection(cmds,
            c => Assert.Equal(100m, Assert.IsType<ModifyOrderCommand>(c).NewLimitPrice),
            c => Assert.Equal(["DE0007164600"], Assert.IsType<CreateWatchlistCommand>(c).Symbols),
            c => Assert.IsType<AddToWatchlistCommand>(c),
            c => Assert.IsType<RemoveFromWatchlistCommand>(c),
            c => Assert.IsType<DeleteWatchlistCommand>(c),
            c => Assert.Equal(AlertCondition.PriceAbove, Assert.IsType<CreateAlertCommand>(c).Condition),
            c => Assert.IsType<DeleteAlertCommand>(c),
            c =>
            {
                var s = Assert.IsType<UpdateSettingsCommand>(c).Settings;
                Assert.Equal("dark", s.Theme);
                Assert.False(s.DataSources.KrakenEnabled);
                Assert.Equal(_server.Queries.Settings.Risk, s.Risk);
            });
        Assert.All(cmds, c =>
        {
            Assert.Equal(CommandSource.Ai, c.Source);
            Assert.Equal(_server.FullClientId, c.ClientId);
            Assert.False(string.IsNullOrEmpty(c.IdempotencyKey));
        });
    }

    [Fact]
    public async Task Read_tools_are_audited()
    {
        await using var client = await _server.ConnectAsync();
        foreach (var tool in new[] { "get_portfolio", "get_positions", "list_orders", "get_trade_history", "get_performance_stats", "list_watchlists", "list_alerts", "get_audit_log", "get_agent_status" })
            Assert.Equal("ok", (await client.CallToolAsync(tool)).Status());
        await client.CallToolAsync("search_symbol", Args(("query", "bit")));

        var entry = Assert.Single(_server.Audit.Entries, e => e.Action == "search_symbol");
        Assert.Equal(CommandSource.Ai, entry.Source);
        Assert.Equal(_server.FullClientId, entry.ClientId);
        Assert.Equal("ok", entry.Result);
        Assert.Contains("\"query\"", entry.ParametersJson);
        Assert.Contains(_server.Audit.Entries, e => e.Action == "get_portfolio");
        Assert.Contains(_server.Audit.Entries, e => e.Action == "get_agent_status");
    }

    [Fact]
    public async Task Get_settings_hides_token_hashes()
    {
        _server.Queries.Settings = await _server.Settings.LoadAsync();
        await using var client = await _server.ConnectAsync();
        var result = await client.CallToolAsync("get_settings");
        var text = result.Json().GetRawText();
        Assert.DoesNotContain("token_hash", text);
        var stored = (await _server.Settings.LoadAsync()).AiClients;
        Assert.All(stored, c => Assert.DoesNotContain(c.TokenHash!, text));
        Assert.Contains(_server.FullClientId, text);
    }

    // ---------------------------------------------------------------- HTTP-Sicherheit

    private HttpRequestMessage InitializeRequest(string? token, string? host = null, string? origin = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, _server.Endpoint)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""", Encoding.UTF8, "application/json"),
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (host is not null) req.Headers.Host = host;
        if (origin is not null) req.Headers.Add("Origin", origin);
        return req;
    }

    [Fact]
    public async Task Missing_or_invalid_token_gives_401()
    {
        using var http = new HttpClient();
        using var none = await http.SendAsync(InitializeRequest(null));
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Contains("Bearer", none.Headers.WwwAuthenticate.ToString());

        using var wrong = await http.SendAsync(InitializeRequest("mdp_falsch"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        using var ok = await http.SendAsync(InitializeRequest(_server.FullToken));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Theory]
    [InlineData("evil.example:80", null)]
    [InlineData("attacker.test:{port}", null)]
    [InlineData("127.0.0.1:1", null)]
    [InlineData("127.0.0.1:{port}", "http://evil.example")]
    [InlineData("127.0.0.1:{port}", "null")]
    [InlineData("localhost:{port}", "https://localhost.evil.example")]
    public async Task Foreign_host_or_origin_gives_403(string host, string? origin)
    {
        using var http = new HttpClient();
        using var res = await http.SendAsync(InitializeRequest(_server.FullToken, host.Replace("{port}", _server.Endpoint.Port.ToString()), origin));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Theory]
    [InlineData("localhost:{port}", null)]
    [InlineData("127.0.0.1:{port}", "http://localhost:3000")]
    [InlineData("127.0.0.1:{port}", "https://127.0.0.1")]
    public async Task Local_host_and_origin_are_allowed(string host, string? origin)
    {
        using var http = new HttpClient();
        using var res = await http.SendAsync(InitializeRequest(_server.FullToken, host.Replace("{port}", _server.Endpoint.Port.ToString()), origin));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Revoked_token_is_rejected()
    {
        await _server.Tokens.RevokeAsync(_server.FullClientId);
        using var http = new HttpClient();
        using var res = await http.SendAsync(InitializeRequest(_server.FullToken));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
