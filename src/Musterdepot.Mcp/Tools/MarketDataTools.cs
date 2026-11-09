using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;

namespace Musterdepot.Mcp.Tools;

public sealed partial class MusterdepotTools
{
    [McpServerTool(Name = "search_symbol", Title = "Symbol suchen", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Sucht handelbare Instrumente (Krypto-Paare wie BTC/EUR, Aktien/ETFs per Name, ISIN oder WKN). " +
                 "Liefert das programmweite 'symbol', das alle anderen Tools erwarten (Krypto z. B. 'BTC/EUR', Aktien die ISIN).")]
    public Task<CallToolResult> SearchSymbol(
        RequestContext<CallToolRequestParams> context,
        [Description("Suchbegriff: Name, Ticker, ISIN oder WKN, z. B. 'bitcoin', 'BTC', 'SAP', 'DE0007164600'.")] string query,
        [Description("Maximale Anzahl Treffer (1–100).")] int limit = 20,
        CancellationToken cancellationToken = default) =>
        ReadAsync(context, async _ =>
        {
            var q = RequireText(query, "query");
            var found = await backend.Queries.SearchSymbolsAsync(q, cancellationToken).ConfigureAwait(false);
            var items = found.Take(Clamp(limit, 1, 100)).ToList();
            return new ToolEnvelope { Status = "ok", Data = new { count = items.Count, instruments = items } };
        }, cancellationToken);

    [McpServerTool(Name = "get_quote", Title = "Kurs abfragen", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Aktueller Kurs eines Instruments (bid, ask, last, mid, change_percent). Jeder Kurs nennt seine Herkunft: " +
                 "'source' (z. B. kraken, gettex), 'delayed_minutes' (0 = Echtzeit; Aktien sind 15–30 Minuten verzögert), " +
                 "'as_of_utc' (Datenstand) und 'is_realtime'. Bei verzögerten Kursen nie so tun, als wären sie aktuell.")]
    public Task<CallToolResult> GetQuote(
        RequestContext<CallToolRequestParams> context,
        [Description("Symbol, z. B. 'BTC/EUR' oder eine ISIN.")] string symbol,
        CancellationToken cancellationToken = default) =>
        ReadAsync(context, _ =>
        {
            var s = RequireText(symbol, "symbol");
            var quote = backend.Queries.GetQuote(s);
            return quote is null
                ? NotFound($"Kein Kurs für '{s}' vorhanden. Symbol mit search_symbol prüfen.")
                : Ok(QuoteView.From(quote, backend.Clock.UtcNow));
        }, cancellationToken);

    [McpServerTool(Name = "get_candles", Title = "Kerzen abfragen", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("OHLCV-Kerzen (Zeitstempel = Kerzenbeginn in UTC), älteste zuerst. Enthält Herkunft ('source') sowie " +
                 "'delayed_minutes'/'is_realtime' des aktuellen Kurses. Kerzen mit 'is_backfill' wurden nach einer Lücke nachgeladen.")]
    public Task<CallToolResult> GetCandles(
        RequestContext<CallToolRequestParams> context,
        [Description("Symbol, z. B. 'BTC/EUR'.")] string symbol,
        [Description("Zeitrahmen: 1m, 5m, 15m, 30m, 1h, 4h, 1d, 1w.")] string timeframe = "1m",
        [Description("Optional: Beginn (ISO 8601 UTC).")] string? from_utc = null,
        [Description("Optional: Ende (ISO 8601 UTC).")] string? to_utc = null,
        [Description("Maximale Anzahl Kerzen (1–1000), die jüngsten werden geliefert.")] int limit = 200,
        CancellationToken cancellationToken = default) =>
        ReadAsync(context, async _ =>
        {
            var s = RequireText(symbol, "symbol");
            var tf = ParseTimeframe(timeframe);
            var from = ParseTime(from_utc, "from_utc");
            var to = ParseTime(to_utc, "to_utc");
            var max = Clamp(limit, 1, 1000);
            var candles = await backend.Queries.GetCandlesAsync(s, tf, from, to, max, cancellationToken).ConfigureAwait(false);
            var list = candles.OrderBy(c => c.OpenTimeUtc).TakeLast(max).ToList();
            var quote = backend.Queries.GetQuote(s);
            return new ToolEnvelope
            {
                Status = "ok",
                Data = new
                {
                    symbol = s,
                    timeframe = JsonNamingTimeframe(tf),
                    source = list.LastOrDefault()?.Source is { Length: > 0 } src ? src : quote?.Source,
                    delayed_minutes = quote?.DelayedMinutes ?? 0,
                    is_realtime = quote?.IsRealtime ?? false,
                    as_of_utc = quote?.AsOfUtc ?? list.LastOrDefault()?.OpenTimeUtc,
                    count = list.Count,
                    candles = list.Select(c => new
                    {
                        open_time_utc = c.OpenTimeUtc,
                        open = c.Open,
                        high = c.High,
                        low = c.Low,
                        close = c.Close,
                        volume = c.Volume,
                        trade_count = c.TradeCount,
                        is_closed = c.IsClosed,
                        is_backfill = c.IsBackfill,
                    }).ToList(),
                },
            };
        }, cancellationToken);

    [McpServerTool(Name = "get_orderbook", Title = "Orderbuch abfragen", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Orderbuch (Bids absteigend, Asks aufsteigend) – nur für Krypto-Paare verfügbar, Aktien haben kein Orderbuch.")]
    public Task<CallToolResult> GetOrderbook(
        RequestContext<CallToolRequestParams> context,
        [Description("Krypto-Symbol, z. B. 'BTC/EUR'.")] string symbol,
        [Description("Anzahl Stufen je Seite (1–100).")] int depth = 10,
        CancellationToken cancellationToken = default) =>
        ReadAsync(context, async _ =>
        {
            var s = RequireText(symbol, "symbol");
            var instruments = await backend.Queries.SearchSymbolsAsync(s, cancellationToken).ConfigureAwait(false);
            var instrument = instruments.FirstOrDefault(i => string.Equals(i.Symbol, s, StringComparison.OrdinalIgnoreCase));
            if (instrument is not null && instrument.AssetClass != AssetClass.Crypto)
                return new ToolEnvelope { Status = "not_found", Message = $"'{s}' ist kein Krypto-Paar – Orderbücher gibt es nur für Krypto." };

            var d = Clamp(depth, 1, 100);
            var book = await backend.Queries.GetOrderBookAsync(s, d, cancellationToken).ConfigureAwait(false);
            if (book is null)
                return new ToolEnvelope { Status = "not_found", Message = $"Kein Orderbuch für '{s}' verfügbar (nur Krypto)." };
            var quote = backend.Queries.GetQuote(s);
            return new ToolEnvelope
            {
                Status = "ok",
                Data = new
                {
                    symbol = book.Symbol,
                    source = book.Source,
                    as_of_utc = book.AsOfUtc,
                    delayed_minutes = quote?.DelayedMinutes ?? 0,
                    is_realtime = quote?.IsRealtime ?? true,
                    best_bid = book.BestBid,
                    best_ask = book.BestAsk,
                    bids = book.Bids.Take(d).Select(l => new { price = l.Price, quantity = l.Quantity }).ToList(),
                    asks = book.Asks.Take(d).Select(l => new { price = l.Price, quantity = l.Quantity }).ToList(),
                },
            };
        }, cancellationToken);

    private static string JsonNamingTimeframe(Timeframe tf) => tf switch
    {
        Timeframe.M1 => "1m", Timeframe.M5 => "5m", Timeframe.M15 => "15m", Timeframe.M30 => "30m",
        Timeframe.H1 => "1h", Timeframe.H4 => "4h", Timeframe.D1 => "1d", Timeframe.W1 => "1w", _ => tf.ToString(),
    };
}

/// <summary>Kurs, wie ihn die KI sieht – Herkunftsfelder sind immer gesetzt.</summary>
public sealed record QuoteView
{
    public required string Symbol { get; init; }
    public decimal? Bid { get; init; }
    public decimal? Ask { get; init; }
    public decimal? Last { get; init; }
    public decimal? Mid { get; init; }
    public decimal? ChangePercent { get; init; }
    public decimal? Volume24h { get; init; }
    public required string Currency { get; init; }
    public required string Source { get; init; }
    public required int DelayedMinutes { get; init; }
    public required DateTimeOffset AsOfUtc { get; init; }
    public required bool IsRealtime { get; init; }
    public required DataQuality Quality { get; init; }
    /// <summary>Alter des Kurses in Sekunden zum Abfragezeitpunkt.</summary>
    public required double AgeSeconds { get; init; }

    public static QuoteView From(QuoteSnapshot q, DateTimeOffset now) => new()
    {
        Symbol = q.Symbol,
        Bid = q.Bid,
        Ask = q.Ask,
        Last = q.Last,
        Mid = q.Mid,
        ChangePercent = q.ChangePercent,
        Volume24h = q.Volume24h,
        Currency = q.Currency,
        Source = q.Source,
        DelayedMinutes = q.DelayedMinutes,
        AsOfUtc = q.AsOfUtc,
        IsRealtime = q.IsRealtime,
        Quality = q.Quality,
        AgeSeconds = Math.Max(0, Math.Round((now - q.AsOfUtc).TotalSeconds, 1)),
    };
}
