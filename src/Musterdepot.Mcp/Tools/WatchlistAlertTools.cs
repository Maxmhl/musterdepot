using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Musterdepot.Core.Commands;

namespace Musterdepot.Mcp.Tools;

public sealed partial class MusterdepotTools
{
    [McpServerTool(Name = "list_watchlists", Title = "Watchlists auflisten", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Alle Watchlists mit ID, Name und Symbolen.")]
    public Task<CallToolResult> ListWatchlists(
        RequestContext<CallToolRequestParams> context,
        CancellationToken cancellationToken = default) =>
        ReadAsync(context, async _ =>
        {
            var lists = await backend.Queries.ListWatchlistsAsync(cancellationToken).ConfigureAwait(false);
            return new ToolEnvelope { Status = "ok", Data = new { count = lists.Count, watchlists = lists } };
        }, cancellationToken);

    [McpServerTool(Name = "list_alerts", Title = "Alarme auflisten", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Alle Kursalarme mit ID, Symbol, Bedingung, Schwelle und ob sie schon ausgelöst wurden.")]
    public Task<CallToolResult> ListAlerts(
        RequestContext<CallToolRequestParams> context,
        [Description("true = nur noch nicht ausgelöste Alarme.")] bool active_only = false,
        CancellationToken cancellationToken = default) =>
        ReadAsync(context, async _ =>
        {
            var alerts = (await backend.Queries.ListAlertsAsync(cancellationToken).ConfigureAwait(false))
                .Where(a => !active_only || a.IsActive).ToList();
            return new ToolEnvelope { Status = "ok", Data = new { count = alerts.Count, alerts } };
        }, cancellationToken);

    [McpServerTool(Name = "create_watchlist", Title = "Watchlist anlegen", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Legt eine neue Watchlist an, optional gleich mit Symbolen.")]
    public Task<CallToolResult> CreateWatchlist(
        RequestContext<CallToolRequestParams> context,
        [Description("Name der Watchlist.")] string name,
        [Description("Optional: Symbole, z. B. ['BTC/EUR', 'ETH/EUR'].")] string[]? symbols = null,
        [Description("Optional: eindeutiger Schlüssel gegen doppeltes Anlegen.")] string? idempotency_key = null,
        CancellationToken cancellationToken = default) =>
        WriteAsync(context, idempotency_key, _ => new CreateWatchlistCommand
        {
            Name = RequireText(name, "name"),
            Symbols = (symbols ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        }, cancellationToken);

    [McpServerTool(Name = "add_to_watchlist", Title = "Zur Watchlist hinzufügen", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Fügt einer Watchlist ein Symbol hinzu.")]
    public Task<CallToolResult> AddToWatchlist(
        RequestContext<CallToolRequestParams> context,
        [Description("ID der Watchlist (aus list_watchlists).")] string watchlist_id,
        [Description("Symbol, z. B. 'BTC/EUR'.")] string symbol,
        [Description("Optional: eindeutiger Schlüssel.")] string? idempotency_key = null,
        CancellationToken cancellationToken = default) =>
        WriteAsync(context, idempotency_key, _ => new AddToWatchlistCommand
        {
            WatchlistId = RequireText(watchlist_id, "watchlist_id"),
            Symbol = RequireText(symbol, "symbol"),
        }, cancellationToken);

    [McpServerTool(Name = "remove_from_watchlist", Title = "Aus Watchlist entfernen", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Entfernt ein Symbol aus einer Watchlist.")]
    public Task<CallToolResult> RemoveFromWatchlist(
        RequestContext<CallToolRequestParams> context,
        [Description("ID der Watchlist.")] string watchlist_id,
        [Description("Symbol.")] string symbol,
        [Description("Optional: eindeutiger Schlüssel.")] string? idempotency_key = null,
        CancellationToken cancellationToken = default) =>
        WriteAsync(context, idempotency_key, _ => new RemoveFromWatchlistCommand
        {
            WatchlistId = RequireText(watchlist_id, "watchlist_id"),
            Symbol = RequireText(symbol, "symbol"),
        }, cancellationToken);

    [McpServerTool(Name = "delete_watchlist", Title = "Watchlist löschen", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Löscht eine Watchlist.")]
    public Task<CallToolResult> DeleteWatchlist(
        RequestContext<CallToolRequestParams> context,
        [Description("ID der Watchlist.")] string watchlist_id,
        [Description("Optional: eindeutiger Schlüssel.")] string? idempotency_key = null,
        CancellationToken cancellationToken = default) =>
        WriteAsync(context, idempotency_key, _ => new DeleteWatchlistCommand { WatchlistId = RequireText(watchlist_id, "watchlist_id") }, cancellationToken);

    [McpServerTool(Name = "create_alert", Title = "Kursalarm anlegen", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Legt einen Kursalarm an, der den Menschen im Programm benachrichtigt. Bedingungen: price_above, price_below, " +
                 "change_percent_above, change_percent_below.")]
    public Task<CallToolResult> CreateAlert(
        RequestContext<CallToolRequestParams> context,
        [Description("Symbol, z. B. 'BTC/EUR'.")] string symbol,
        [Description("price_above, price_below, change_percent_above oder change_percent_below.")] string condition,
        [Description("Schwelle: Preis in Quote-Währung bzw. Prozent.")] decimal threshold,
        [Description("Optional: Notiz.")] string? note = null,
        [Description("Optional: eindeutiger Schlüssel gegen doppeltes Anlegen.")] string? idempotency_key = null,
        CancellationToken cancellationToken = default) =>
        WriteAsync(context, idempotency_key, _ => new CreateAlertCommand
        {
            Symbol = RequireText(symbol, "symbol"),
            Condition = ParseEnum<AlertCondition>(condition, "condition"),
            Threshold = threshold,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
        }, cancellationToken);

    [McpServerTool(Name = "delete_alert", Title = "Kursalarm löschen", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Löscht einen Kursalarm.")]
    public Task<CallToolResult> DeleteAlert(
        RequestContext<CallToolRequestParams> context,
        [Description("ID des Alarms (aus list_alerts).")] string alert_id,
        [Description("Optional: eindeutiger Schlüssel.")] string? idempotency_key = null,
        CancellationToken cancellationToken = default) =>
        WriteAsync(context, idempotency_key, _ => new DeleteAlertCommand { AlertId = RequireText(alert_id, "alert_id") }, cancellationToken);
}
