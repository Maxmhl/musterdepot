using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Musterdepot.Core.Commands;
using Musterdepot.Core.Domain;

namespace Musterdepot.Mcp.Tools;

public sealed partial class MusterdepotTools
{
    [McpServerTool(Name = "place_order", Title = "Order aufgeben", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Gibt eine Paper-Trading-Order auf (Spielgeld, keine echte Börse). Pflicht: 'idempotency_key' – ein von dir gewählter, " +
                 "eindeutiger Schlüssel je Order-Absicht; bei Wiederholung mit gleichem Schlüssel entsteht keine zweite Order (Status 'duplicate'). " +
                 "Menge entweder als 'quantity' (Stück) oder 'notional' (Betrag in EUR). Ordertypen: market, limit (limit_price), stop (stop_price), " +
                 "stop_limit (stop_price + limit_price), trailing_stop (trail_amount oder trail_percent). " +
                 "'dry_run': true liefert nur eine Vorschau mit geschätztem Preis, Gebühren und Risikoprüfung, ohne Order. " +
                 "Bracket: take_profit/stop_loss erzeugen Gegenorders nach Ausführung; oco_group verknüpft Orders (eine gefüllt → andere storniert). " +
                 "Kein Leerverkauf. Antwort-Status: executed, previewed, pending_approval (Mensch muss im Programm freigeben), rejected, denied, duplicate, failed.")]
    public Task<CallToolResult> PlaceOrder(
        RequestContext<CallToolRequestParams> context,
        [Description("Symbol, z. B. 'BTC/EUR' oder eine ISIN.")] string symbol,
        [Description("buy oder sell.")] string side,
        [Description("market, limit, stop, stop_limit oder trailing_stop.")] string order_type,
        [Description("Eindeutiger Schlüssel dieser Order-Absicht (z. B. eine UUID). Pflichtfeld, verhindert Doppelorders.")] string idempotency_key,
        [Description("Stückzahl (alternativ zu notional).")] decimal? quantity = null,
        [Description("Betrag in EUR statt Stückzahl (alternativ zu quantity).")] decimal? notional = null,
        [Description("Limitpreis (limit, stop_limit).")] decimal? limit_price = null,
        [Description("Stop-/Auslösepreis (stop, stop_limit).")] decimal? stop_price = null,
        [Description("Trailing-Abstand absolut in Preiseinheiten (trailing_stop).")] decimal? trail_amount = null,
        [Description("Trailing-Abstand in Prozent, z. B. 2 = 2 % (trailing_stop).")] decimal? trail_percent = null,
        [Description("Gültigkeit: day (Standard), gtc, ioc.")] string? time_in_force = null,
        [Description("Optional: Ablaufzeitpunkt (ISO 8601 UTC).")] string? expires_utc = null,
        [Description("Optional: Strategie-Tag für die Statistik, z. B. 'breakout-v1'.")] string? strategy_tag = null,
        [Description("Optional (Bracket): Take-Profit-Limitpreis der Gegenorder.")] decimal? take_profit = null,
        [Description("Optional (Bracket): Stop-Loss-Preis der Gegenorder.")] decimal? stop_loss = null,
        [Description("Optional: OCO-Gruppe – Orders mit gleicher Gruppe stornieren sich gegenseitig.")] string? oco_group = null,
        [Description("true = nur Vorschau (Gebühren, Risikoprüfung), keine Order.")] bool dry_run = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idempotency_key))
            return Task.FromResult(new ToolEnvelope
            {
                Status = "invalid_arguments",
                Message = "idempotency_key fehlt. Wähle einen eindeutigen Schlüssel je Order (z. B. eine UUID) und verwende ihn bei Wiederholungen erneut.",
            }.ToResult(true));

        return WriteAsync(context, idempotency_key, _ =>
        {
            var type = ParseEnum<OrderType>(order_type, "order_type");
            if (quantity is null == notional is null)
                throw new ToolArgumentException("Genau eines von quantity (Stück) oder notional (EUR-Betrag) angeben.");
            Positive(quantity, "quantity");
            Positive(notional, "notional");
            Positive(limit_price, "limit_price");
            Positive(stop_price, "stop_price");
            Positive(trail_amount, "trail_amount");
            Positive(trail_percent, "trail_percent");
            Positive(take_profit, "take_profit");
            Positive(stop_loss, "stop_loss");
            if (type is OrderType.Limit or OrderType.StopLimit && limit_price is null)
                throw new ToolArgumentException("limit_price ist für limit und stop_limit erforderlich.");
            if (type is OrderType.Stop or OrderType.StopLimit && stop_price is null)
                throw new ToolArgumentException("stop_price ist für stop und stop_limit erforderlich.");
            if (type is OrderType.TrailingStop && trail_amount is null == trail_percent is null)
                throw new ToolArgumentException("trailing_stop braucht genau eines von trail_amount oder trail_percent.");

            return new PlaceOrderCommand
            {
                Symbol = RequireText(symbol, "symbol"),
                Side = ParseEnum<OrderSide>(side, "side"),
                Type = type,
                Quantity = quantity,
                Notional = notional,
                LimitPrice = limit_price,
                StopPrice = stop_price,
                TrailAmount = trail_amount,
                TrailPercent = trail_percent,
                TimeInForce = ParseEnum<TimeInForce>(time_in_force, "time_in_force", TimeInForce.Day),
                ExpiresUtc = ParseTime(expires_utc, "expires_utc"),
                StrategyTag = string.IsNullOrWhiteSpace(strategy_tag) ? null : strategy_tag.Trim(),
                TakeProfitPrice = take_profit,
                StopLossPrice = stop_loss,
                OcoGroupId = string.IsNullOrWhiteSpace(oco_group) ? null : oco_group.Trim(),
                DryRun = dry_run,
            };
        }, cancellationToken);
    }

    [McpServerTool(Name = "modify_order", Title = "Order ändern", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Ändert eine offene Order (Menge, Limit- oder Stoppreis). Nur angegebene Felder werden geändert.")]
    public Task<CallToolResult> ModifyOrder(
        RequestContext<CallToolRequestParams> context,
        [Description("ID der Order (aus list_orders oder place_order).")] string order_id,
        [Description("Neue Gesamtstückzahl.")] decimal? new_quantity = null,
        [Description("Neuer Limitpreis.")] decimal? new_limit_price = null,
        [Description("Neuer Stoppreis.")] decimal? new_stop_price = null,
        [Description("Optional: eindeutiger Schlüssel gegen doppelte Ausführung.")] string? idempotency_key = null,
        CancellationToken cancellationToken = default) =>
        WriteAsync(context, idempotency_key, _ =>
        {
            if (new_quantity is null && new_limit_price is null && new_stop_price is null)
                throw new ToolArgumentException("Mindestens eines von new_quantity, new_limit_price, new_stop_price angeben.");
            return new ModifyOrderCommand
            {
                OrderId = RequireText(order_id, "order_id"),
                NewQuantity = Positive(new_quantity, "new_quantity"),
                NewLimitPrice = Positive(new_limit_price, "new_limit_price"),
                NewStopPrice = Positive(new_stop_price, "new_stop_price"),
            };
        }, cancellationToken);

    [McpServerTool(Name = "cancel_order", Title = "Order stornieren", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Storniert eine offene Order.")]
    public Task<CallToolResult> CancelOrder(
        RequestContext<CallToolRequestParams> context,
        [Description("ID der Order.")] string order_id,
        [Description("Optional: eindeutiger Schlüssel gegen doppelte Ausführung.")] string? idempotency_key = null,
        CancellationToken cancellationToken = default) =>
        WriteAsync(context, idempotency_key, _ => new CancelOrderCommand { OrderId = RequireText(order_id, "order_id") }, cancellationToken);

    [McpServerTool(Name = "list_orders", Title = "Orders auflisten", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Listet Orders (Standard: nur offene), neueste zuerst, mit Status, Füllmenge, Durchschnittspreis, Gebühren, Quelle (human/ai) und Strategie-Tag.")]
    public Task<CallToolResult> ListOrders(
        RequestContext<CallToolRequestParams> context,
        [Description("true = nur offene Orders, false = alle.")] bool open_only = true,
        [Description("Optional: nur dieses Symbol.")] string? symbol = null,
        [Description("Maximale Anzahl (1–500).")] int limit = 100,
        CancellationToken cancellationToken = default) =>
        ReadAsync(context, _ =>
        {
            IEnumerable<Order> orders = backend.Queries.ListOrders(open_only);
            if (!string.IsNullOrWhiteSpace(symbol))
                orders = orders.Where(o => string.Equals(o.Symbol, symbol.Trim(), StringComparison.OrdinalIgnoreCase));
            var list = orders.OrderByDescending(o => o.CreatedUtc).Take(Clamp(limit, 1, 500)).ToList();
            return Ok(new { count = list.Count, orders = list });
        }, cancellationToken);
}
