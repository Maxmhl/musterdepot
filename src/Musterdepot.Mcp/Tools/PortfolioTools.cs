using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Musterdepot.Core.Domain;

namespace Musterdepot.Mcp.Tools;

public sealed partial class MusterdepotTools
{
    [McpServerTool(Name = "get_portfolio", Title = "Depot abfragen", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Depotübersicht in EUR: Cash, reserviertes Cash, Positionswert, Equity, realisierte/offene GuV, Tages-GuV, Gebühren, " +
                 "Not-Aus- und Tagesverlust-Status sowie alle Positionen. 'price_is_delayed' markiert Positionen mit verzögertem Kurs.")]
    public Task<CallToolResult> GetPortfolio(
        RequestContext<CallToolRequestParams> context,
        CancellationToken cancellationToken = default) =>
        ReadAsync(context, _ => Ok(backend.Queries.GetPortfolio()), cancellationToken);

    [McpServerTool(Name = "get_positions", Title = "Positionen abfragen", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Offene Positionen mit Menge, Einstand (FIFO), letztem Kurs, Marktwert und offener/realisierter GuV.")]
    public Task<CallToolResult> GetPositions(
        RequestContext<CallToolRequestParams> context,
        [Description("Optional: nur dieses Symbol.")] string? symbol = null,
        CancellationToken cancellationToken = default) =>
        ReadAsync(context, _ =>
        {
            IEnumerable<PositionSnapshot> positions = backend.Queries.GetPositions();
            if (!string.IsNullOrWhiteSpace(symbol))
                positions = positions.Where(p => string.Equals(p.Symbol, symbol.Trim(), StringComparison.OrdinalIgnoreCase));
            var list = positions.ToList();
            return Ok(new { count = list.Count, positions = list });
        }, cancellationToken);

    [McpServerTool(Name = "get_trade_history", Title = "Ausführungen abfragen", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Ausführungen (Fills), neueste zuerst: Preis, Menge, Gebühr, realisierte GuV, Qualität (order_book, trade, synthetic, gap), " +
                 "Quelle (human/ai) und Strategie-Tag. 'executed_during_gap' = während einer Datenlücke ausgeführt.")]
    public Task<CallToolResult> GetTradeHistory(
        RequestContext<CallToolRequestParams> context,
        [Description("Optional: ab Zeitpunkt (ISO 8601 UTC).")] string? from_utc = null,
        [Description("Optional: bis Zeitpunkt (ISO 8601 UTC).")] string? to_utc = null,
        [Description("Optional: nur dieses Symbol.")] string? symbol = null,
        [Description("Maximale Anzahl (1–1000).")] int limit = 100,
        CancellationToken cancellationToken = default) =>
        ReadAsync(context, _ =>
        {
            IEnumerable<Fill> fills = backend.Queries.GetTradeHistory(ParseTime(from_utc, "from_utc"), ParseTime(to_utc, "to_utc"));
            if (!string.IsNullOrWhiteSpace(symbol))
                fills = fills.Where(f => string.Equals(f.Symbol, symbol.Trim(), StringComparison.OrdinalIgnoreCase));
            var list = fills.OrderByDescending(f => f.TimestampUtc).Take(Clamp(limit, 1, 1000)).ToList();
            return Ok(new { count = list.Count, fills = list });
        }, cancellationToken);

    [McpServerTool(Name = "get_performance_stats", Title = "Statistik abfragen", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Kennzahlen: Trades, Gewinnquote, Profit Factor, Erwartungswert, max. Drawdown, Haltedauer, Gebührenanteil. " +
                 "Filterbar nach Quelle (human, ai, all) und Strategie-Tag – z. B. um KI- und eigene Trades zu vergleichen.")]
    public Task<CallToolResult> GetPerformanceStats(
        RequestContext<CallToolRequestParams> context,
        [Description("Quelle: all (Standard), human oder ai.")] string? source = null,
        [Description("Optional: nur dieser Strategie-Tag.")] string? strategy_tag = null,
        CancellationToken cancellationToken = default) =>
        ReadAsync(context, _ =>
        {
            CommandSource? src = string.IsNullOrWhiteSpace(source) || source.Trim().Equals("all", StringComparison.OrdinalIgnoreCase)
                ? null
                : ParseEnum<CommandSource>(source, "source");
            var tag = string.IsNullOrWhiteSpace(strategy_tag) ? null : strategy_tag.Trim();
            return Ok(backend.Queries.GetPerformanceStats(src, tag));
        }, cancellationToken);
}
