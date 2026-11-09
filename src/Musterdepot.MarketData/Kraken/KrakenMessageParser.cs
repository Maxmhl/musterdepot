using System.Text;
using System.Text.Json;
using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;
using Musterdepot.MarketData.Infrastructure;
using Musterdepot.MarketData.Sources;

namespace Musterdepot.MarketData.Kraken;

/// <summary>
/// Parser für Kraken WebSocket v2 (wss://ws.kraken.com/v2): Kanäle ticker, trade, ohlc, book, heartbeat, status
/// sowie Antworten auf subscribe/unsubscribe/ping.
/// </summary>
public sealed class KrakenMessageParser(string sourceName = "kraken")
{
    public string SourceName { get; } = sourceName;

    public ParsedWsMessage Parse(string json, DateTimeOffset receivedUtc)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return ParsedWsMessage.Of(WsMessageKind.Ignored);

        if (JsonRead.Str(root, "method") is { } method)
        {
            var success = JsonRead.Bool(root, "success");
            var error = JsonRead.Str(root, "error");
            return method switch
            {
                "pong" => ParsedWsMessage.Of(WsMessageKind.Pong),
                "subscribe" or "unsubscribe" when success != false && error is null =>
                    ParsedWsMessage.Of(WsMessageKind.SubscriptionAck, ResultDescription(root)) with { Channel = ChannelOfResult(root) },
                _ when success == false || error is not null =>
                    ParsedWsMessage.Of(WsMessageKind.Error, $"{method}: {error ?? "unbekannter Fehler"}") with { Channel = ChannelOfResult(root) },
                _ => ParsedWsMessage.Of(WsMessageKind.Ignored),
            };
        }

        var channel = JsonRead.Str(root, "channel");
        var type = JsonRead.Str(root, "type");
        if (channel is null)
            return JsonRead.Str(root, "error") is { } err ? ParsedWsMessage.Of(WsMessageKind.Error, err) : ParsedWsMessage.Of(WsMessageKind.Ignored);

        switch (channel)
        {
            case "heartbeat":
                return ParsedWsMessage.Of(WsMessageKind.Heartbeat) with { Channel = channel };
            case "status":
            {
                string? system = null;
                if (root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array && d.GetArrayLength() > 0)
                    system = JsonRead.Str(d[0], "system");
                return ParsedWsMessage.Of(WsMessageKind.Status, system) with { Channel = channel };
            }
        }

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return ParsedWsMessage.Of(WsMessageKind.Ignored) with { Channel = channel };

        var msgTime = JsonRead.Time(root, "timestamp");
        var events = new List<MarketEvent>();
        switch (channel)
        {
            case "ticker":
                foreach (var t in data.EnumerateArray()) events.Add(ParseTicker(t, receivedUtc, msgTime));
                break;
            case "trade":
                foreach (var t in data.EnumerateArray()) events.Add(ParseTrade(t, receivedUtc));
                break;
            case "ohlc":
                ParseOhlc(data, type == "snapshot", receivedUtc, msgTime, events);
                break;
            case "book":
                foreach (var b in data.EnumerateArray()) events.Add(ParseBook(b, type == "snapshot", receivedUtc, msgTime));
                break;
            default:
                return ParsedWsMessage.Of(WsMessageKind.Ignored) with { Channel = channel };
        }
        return new ParsedWsMessage(WsMessageKind.Data, events) { Channel = channel };
    }

    private QuoteTick ParseTicker(JsonElement t, DateTimeOffset received, DateTimeOffset? msgTime) => new()
    {
        Symbol = JsonRead.Str(t, "symbol")!,
        Source = SourceName,
        AsOfUtc = JsonRead.Time(t, "timestamp") ?? msgTime ?? received,
        ReceivedUtc = received,
        Bid = JsonRead.DecOrNull(t, "bid"),
        Ask = JsonRead.DecOrNull(t, "ask"),
        BidSize = JsonRead.DecOrNull(t, "bid_qty"),
        AskSize = JsonRead.DecOrNull(t, "ask_qty"),
        Last = JsonRead.DecOrNull(t, "last"),
        Volume24h = JsonRead.DecOrNull(t, "volume"),
        ChangePercent = JsonRead.DecOrNull(t, "change_pct"),
        High24h = JsonRead.DecOrNull(t, "high"),
        Low24h = JsonRead.DecOrNull(t, "low"),
    };

    private TradeTick ParseTrade(JsonElement t, DateTimeOffset received) => new()
    {
        Symbol = JsonRead.Str(t, "symbol")!,
        Source = SourceName,
        AsOfUtc = JsonRead.Time(t, "timestamp") ?? received,
        ReceivedUtc = received,
        Price = JsonRead.Dec(t.GetProperty("price")),
        Quantity = JsonRead.Dec(t.GetProperty("qty")),
        AggressorSide = JsonRead.Str(t, "side") switch { "buy" => OrderSide.Buy, "sell" => OrderSide.Sell, _ => null },
        TradeId = JsonRead.Str(t, "trade_id"),
    };

    private void ParseOhlc(JsonElement data, bool snapshot, DateTimeOffset received, DateTimeOffset? msgTime, List<MarketEvent> events)
    {
        var candles = new List<(Candle Candle, DateTimeOffset AsOf)>();
        foreach (var c in data.EnumerateArray())
        {
            var interval = (int)(JsonRead.Long(c, "interval") ?? 1);
            var symbol = JsonRead.Str(c, "symbol")!;
            var open = JsonRead.Time(c, "interval_begin") ?? throw new FormatException("ohlc ohne interval_begin");
            candles.Add((new Candle
            {
                Symbol = symbol,
                Timeframe = TimeframeFromMinutes(interval),
                OpenTimeUtc = open,
                Open = JsonRead.Dec(c.GetProperty("open")),
                High = JsonRead.Dec(c.GetProperty("high")),
                Low = JsonRead.Dec(c.GetProperty("low")),
                Close = JsonRead.Dec(c.GetProperty("close")),
                Volume = JsonRead.DecOrNull(c, "volume") ?? 0m,
                TradeCount = (int)(JsonRead.Long(c, "trades") ?? 0),
                Source = SourceName,
                IsClosed = false,
            }, JsonRead.Time(c, "timestamp") ?? msgTime ?? received));
        }
        // Im Snapshot sind alle Kerzen außer der jüngsten je Symbol abgeschlossen.
        var latest = candles.GroupBy(c => c.Candle.Symbol).ToDictionary(g => g.Key, g => g.Max(x => x.Candle.OpenTimeUtc));
        foreach (var (candle, asOf) in candles.OrderBy(c => c.Candle.OpenTimeUtc))
        {
            var closed = snapshot && candle.OpenTimeUtc < latest[candle.Symbol];
            events.Add(new CandleUpdate
            {
                Symbol = candle.Symbol,
                Source = SourceName,
                AsOfUtc = asOf,
                ReceivedUtc = received,
                Candle = closed ? candle with { IsClosed = true } : candle,
            });
        }
    }

    private OrderBookUpdate ParseBook(JsonElement b, bool snapshot, DateTimeOffset received, DateTimeOffset? msgTime) => new()
    {
        Symbol = JsonRead.Str(b, "symbol")!,
        Source = SourceName,
        AsOfUtc = JsonRead.Time(b, "timestamp") ?? msgTime ?? received,
        ReceivedUtc = received,
        IsSnapshot = snapshot,
        Bids = Levels(b, "bids"),
        Asks = Levels(b, "asks"),
        Checksum = JsonRead.Long(b, "checksum"),
    };

    private static IReadOnlyList<OrderBookLevel> Levels(JsonElement b, string name)
    {
        if (!b.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return [];
        var list = new List<OrderBookLevel>(arr.GetArrayLength());
        foreach (var l in arr.EnumerateArray())
            list.Add(new OrderBookLevel(JsonRead.Dec(l.GetProperty("price")), JsonRead.Dec(l.GetProperty("qty"))));
        return list;
    }

    private static string? ChannelOfResult(JsonElement root)
        => root.TryGetProperty("result", out var r) ? JsonRead.Str(r, "channel") : null;

    private static string? ResultDescription(JsonElement root)
        => root.TryGetProperty("result", out var r) ? $"{JsonRead.Str(r, "channel")} {JsonRead.Str(r, "symbol")}".Trim() : null;

    public static Timeframe TimeframeFromMinutes(int minutes) => minutes switch
    {
        1 => Timeframe.M1, 5 => Timeframe.M5, 15 => Timeframe.M15, 30 => Timeframe.M30,
        60 => Timeframe.H1, 240 => Timeframe.H4, 1440 => Timeframe.D1, 10080 => Timeframe.W1,
        _ => throw new FormatException($"Unbekanntes Kraken-Intervall {minutes}")
    };

    // ---------- Nachrichten bauen ----------
    private static int _reqId;
    public static int NextReqId() => Interlocked.Increment(ref _reqId);

    public static string BuildSubscribe(string channel, IEnumerable<string> symbols, int? depth = null, int? interval = null, bool? snapshot = null, string method = "subscribe")
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("method", method);
            w.WriteStartObject("params");
            w.WriteString("channel", channel);
            w.WriteStartArray("symbol");
            foreach (var s in symbols) w.WriteStringValue(s);
            w.WriteEndArray();
            if (depth is { } d) w.WriteNumber("depth", d);
            if (interval is { } i) w.WriteNumber("interval", i);
            if (snapshot is { } sn) w.WriteBoolean("snapshot", sn);
            w.WriteEndObject();
            w.WriteNumber("req_id", NextReqId());
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static string BuildPing() => $"{{\"method\":\"ping\",\"req_id\":{NextReqId()}}}";
}

/// <summary>
/// Kraken-v2-Prüfsumme: je 10 beste Asks (aufsteigend), dann 10 beste Bids (absteigend); Preis und Menge mit der
/// Präzision des Paares formatiert, Punkt entfernt, führende Nullen entfernt, alles verkettet → CRC32.
/// </summary>
public static class KrakenChecksum
{
    public static uint Compute(IReadOnlyList<OrderBookLevel> asksAscending, IReadOnlyList<OrderBookLevel> bidsDescending, int pricePrecision, int qtyPrecision)
    {
        var sb = new StringBuilder(512);
        foreach (var l in asksAscending.Take(10)) Append(sb, l, pricePrecision, qtyPrecision);
        foreach (var l in bidsDescending.Take(10)) Append(sb, l, pricePrecision, qtyPrecision);
        return Crc32.Compute(sb.ToString());
    }

    private static void Append(StringBuilder sb, OrderBookLevel l, int pp, int qp)
    {
        sb.Append(Format(l.Price, pp));
        sb.Append(Format(l.Quantity, qp));
    }

    public static string Format(decimal value, int precision)
    {
        var s = Math.Round(value, precision, MidpointRounding.AwayFromZero)
            .ToString("F" + precision, System.Globalization.CultureInfo.InvariantCulture)
            .Replace(".", "", StringComparison.Ordinal)
            .TrimStart('0');
        return s.Length == 0 ? "0" : s;
    }
}
