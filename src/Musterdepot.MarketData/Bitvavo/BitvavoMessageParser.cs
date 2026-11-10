using System.Text.Json;
using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;
using Musterdepot.MarketData.Infrastructure;
using Musterdepot.MarketData.Sources;

namespace Musterdepot.MarketData.Bitvavo;

/// <summary>
/// Parser für Bitvavo WebSocket v2 (wss://ws.bitvavo.com/v2/): Ereignisse ticker, ticker24h, trade, candle, book
/// sowie Antworten auf subscribe, getBook und getTime. Märkte "BTC-EUR" werden zu "BTC/EUR".
/// </summary>
public sealed class BitvavoMessageParser(string sourceName = "bitvavo")
{
    public string SourceName { get; } = sourceName;

    public ParsedWsMessage Parse(string json, DateTimeOffset receivedUtc)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return ParsedWsMessage.Of(WsMessageKind.Ignored);

        if (JsonRead.Str(root, "error") is { } error)
            return ParsedWsMessage.Of(WsMessageKind.Error, $"{JsonRead.Str(root, "errorCode")}: {error}") with { Channel = JsonRead.Str(root, "action") };

        if (JsonRead.Str(root, "action") is { } action)
        {
            switch (action)
            {
                case "getTime":
                    return ParsedWsMessage.Of(WsMessageKind.Pong);
                case "getBook" when root.TryGetProperty("response", out var resp):
                {
                    var book = ParseBook(resp, true, receivedUtc);
                    return new ParsedWsMessage(WsMessageKind.Data, [book]) { Channel = "book", Sequence = JsonRead.Long(resp, "nonce") };
                }
                default:
                    return ParsedWsMessage.Of(WsMessageKind.Ignored) with { Channel = action };
            }
        }

        var ev = JsonRead.Str(root, "event");
        switch (ev)
        {
            case "subscribed":
            case "unsubscribed":
                return ParsedWsMessage.Of(WsMessageKind.SubscriptionAck, ev) with { Channel = ev };
            case "authenticate":
                return ParsedWsMessage.Of(WsMessageKind.Ignored);
            case "ticker":
                return Data("ticker", ParseTicker(root, receivedUtc));
            case "ticker24h":
            {
                var list = new List<MarketEvent>();
                if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    foreach (var t in data.EnumerateArray()) list.Add(ParseTicker24h(t, receivedUtc));
                return new ParsedWsMessage(WsMessageKind.Data, list) { Channel = "ticker24h" };
            }
            case "trade":
                return Data("trades", ParseTrade(root, receivedUtc));
            case "candle":
                return new ParsedWsMessage(WsMessageKind.Data, ParseCandles(root, receivedUtc)) { Channel = "candles" };
            case "book":
            {
                var book = ParseBook(root, false, receivedUtc);
                return new ParsedWsMessage(WsMessageKind.Data, [book]) { Channel = "book", Sequence = JsonRead.Long(root, "nonce") };
            }
            default:
                return ParsedWsMessage.Of(WsMessageKind.Ignored) with { Channel = ev };
        }
    }

    private static ParsedWsMessage Data(string channel, MarketEvent e) => new(WsMessageKind.Data, [e]) { Channel = channel };

    private QuoteTick ParseTicker(JsonElement t, DateTimeOffset received) => new()
    {
        Symbol = SymbolHelper.FromDashed(JsonRead.Str(t, "market")!),
        Source = SourceName,
        AsOfUtc = JsonRead.Long(t, "timestamp") is { } ms ? JsonRead.FromUnixMs(ms) : received,
        ReceivedUtc = received,
        Bid = JsonRead.DecOrNull(t, "bestBid"),
        BidSize = JsonRead.DecOrNull(t, "bestBidSize"),
        Ask = JsonRead.DecOrNull(t, "bestAsk"),
        AskSize = JsonRead.DecOrNull(t, "bestAskSize"),
        Last = JsonRead.DecOrNull(t, "lastPrice"),
    };

    private QuoteTick ParseTicker24h(JsonElement t, DateTimeOffset received)
    {
        var open = JsonRead.DecOrNull(t, "open");
        var last = JsonRead.DecOrNull(t, "last");
        decimal? change = open is > 0 && last is { } l ? Math.Round((l - open.Value) / open.Value * 100m, 4) : null;
        return new QuoteTick
        {
            Symbol = SymbolHelper.FromDashed(JsonRead.Str(t, "market")!),
            Source = SourceName,
            AsOfUtc = JsonRead.Long(t, "timestamp") is { } ms ? JsonRead.FromUnixMs(ms) : received,
            ReceivedUtc = received,
            Bid = JsonRead.DecOrNull(t, "bid"),
            BidSize = JsonRead.DecOrNull(t, "bidSize"),
            Ask = JsonRead.DecOrNull(t, "ask"),
            AskSize = JsonRead.DecOrNull(t, "askSize"),
            Last = last,
            Volume24h = JsonRead.DecOrNull(t, "volume"),
            High24h = JsonRead.DecOrNull(t, "high"),
            Low24h = JsonRead.DecOrNull(t, "low"),
            ChangePercent = change,
        };
    }

    private TradeTick ParseTrade(JsonElement t, DateTimeOffset received) => new()
    {
        Symbol = SymbolHelper.FromDashed(JsonRead.Str(t, "market")!),
        Source = SourceName,
        AsOfUtc = JsonRead.Long(t, "timestamp") is { } ms ? JsonRead.FromUnixMs(ms) : received,
        ReceivedUtc = received,
        Price = JsonRead.Dec(t.GetProperty("price")),
        Quantity = JsonRead.Dec(t.GetProperty("amount")),
        // Bitvavo meldet die Seite des Takers.
        AggressorSide = JsonRead.Str(t, "side") switch { "buy" => OrderSide.Buy, "sell" => OrderSide.Sell, _ => null },
        TradeId = JsonRead.Str(t, "id"),
    };

    private List<MarketEvent> ParseCandles(JsonElement root, DateTimeOffset received)
    {
        var symbol = SymbolHelper.FromDashed(JsonRead.Str(root, "market")!);
        var tf = TimeframeFromInterval(JsonRead.Str(root, "interval") ?? "1m");
        var list = new List<MarketEvent>();
        if (!root.TryGetProperty("candle", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var row in arr.EnumerateArray())
        {
            var c = ParseCandleRow(row, symbol, tf, SourceName) with { IsClosed = false };
            list.Add(new CandleUpdate { Symbol = symbol, Source = SourceName, AsOfUtc = received, ReceivedUtc = received, Candle = c });
        }
        return list;
    }

    private OrderBookUpdate ParseBook(JsonElement b, bool snapshot, DateTimeOffset received) => new()
    {
        Symbol = SymbolHelper.FromDashed(JsonRead.Str(b, "market")!),
        Source = SourceName,
        AsOfUtc = received,
        ReceivedUtc = received,
        IsSnapshot = snapshot,
        Bids = Levels(b, "bids"),
        Asks = Levels(b, "asks"),
    };

    internal static IReadOnlyList<OrderBookLevel> Levels(JsonElement b, string name)
    {
        if (!b.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return [];
        return arr.EnumerateArray().Select(l => new OrderBookLevel(JsonRead.Dec(l[0]), JsonRead.Dec(l[1]))).ToList();
    }

    /// <summary>[timestamp_ms, open, high, low, close, volume]</summary>
    public static Candle ParseCandleRow(JsonElement row, string symbol, Timeframe tf, string source) => new()
    {
        Symbol = symbol,
        Timeframe = tf,
        OpenTimeUtc = JsonRead.FromUnixMs((long)JsonRead.Dec(row[0])),
        Open = JsonRead.Dec(row[1]),
        High = JsonRead.Dec(row[2]),
        Low = JsonRead.Dec(row[3]),
        Close = JsonRead.Dec(row[4]),
        Volume = JsonRead.Dec(row[5]),
        Source = source,
    };

    public static Timeframe TimeframeFromInterval(string interval) => interval switch
    {
        "1m" => Timeframe.M1, "5m" => Timeframe.M5, "15m" => Timeframe.M15, "30m" => Timeframe.M30,
        "1h" => Timeframe.H1, "4h" => Timeframe.H4, "1d" => Timeframe.D1,
        _ => throw new FormatException($"Unbekanntes Bitvavo-Intervall {interval}")
    };

    /// <summary>Bitvavo-Intervall für einen Zeitrahmen; W1 gibt es nicht (wird aus 1d gebildet).</summary>
    public static string? IntervalOf(Timeframe tf) => tf switch
    {
        Timeframe.M1 => "1m", Timeframe.M5 => "5m", Timeframe.M15 => "15m", Timeframe.M30 => "30m",
        Timeframe.H1 => "1h", Timeframe.H4 => "4h", Timeframe.D1 => "1d", _ => null
    };

    // ---------- Nachrichten bauen ----------
    public static string BuildSubscribe(IEnumerable<string> symbols, bool book, string action = "subscribe")
    {
        var markets = symbols.Select(SymbolHelper.ToDashed).ToArray();
        var channels = new List<object>
        {
            new { name = "ticker", markets },
            new { name = "ticker24h", markets },
            new { name = "trades", markets },
            new { name = "candles", interval = new[] { "1m" }, markets },
        };
        if (book) channels.Add(new { name = "book", markets });
        return JsonSerializer.Serialize(new { action, channels });
    }

    public static string BuildGetBook(string symbol, int depth)
        => JsonSerializer.Serialize(new { action = "getBook", market = SymbolHelper.ToDashed(symbol), depth });

    public static string BuildGetTime() => "{\"action\":\"getTime\"}";
}
