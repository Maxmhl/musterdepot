using System.Text.Json;
using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;
using Musterdepot.MarketData.Infrastructure;
using Musterdepot.MarketData.Sources;

namespace Musterdepot.MarketData.Coinbase;

/// <summary>
/// Parser für den Coinbase-Exchange-Feed (wss://ws-feed.exchange.coinbase.com): ticker, match/last_match,
/// snapshot/l2update (level2_batch), heartbeat, subscriptions, error.
/// </summary>
public sealed class CoinbaseMessageParser(string sourceName = "coinbase")
{
    public string SourceName { get; } = sourceName;

    public ParsedWsMessage Parse(string json, DateTimeOffset receivedUtc)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return ParsedWsMessage.Of(WsMessageKind.Ignored);
        var type = JsonRead.Str(root, "type");
        switch (type)
        {
            case "subscriptions":
                return ParsedWsMessage.Of(WsMessageKind.SubscriptionAck) with { Channel = type };
            case "heartbeat":
                return ParsedWsMessage.Of(WsMessageKind.Heartbeat) with { Channel = type, Sequence = JsonRead.Long(root, "sequence") };
            case "error":
                return ParsedWsMessage.Of(WsMessageKind.Error, $"{JsonRead.Str(root, "message")} {JsonRead.Str(root, "reason")}".Trim());
            case "ticker":
                return Data(type, ParseTicker(root, receivedUtc), root);
            case "match":
            case "last_match":
                return Data("matches", ParseMatch(root, receivedUtc), root);
            case "snapshot":
                return Data("level2", new OrderBookUpdate
                {
                    Symbol = Sym(root),
                    Source = SourceName,
                    AsOfUtc = JsonRead.Time(root, "time") ?? receivedUtc,
                    ReceivedUtc = receivedUtc,
                    IsSnapshot = true,
                    Bids = Levels(root, "bids"),
                    Asks = Levels(root, "asks"),
                }, root);
            case "l2update":
            {
                var bids = new List<OrderBookLevel>();
                var asks = new List<OrderBookLevel>();
                if (root.TryGetProperty("changes", out var changes))
                    foreach (var c in changes.EnumerateArray())
                    {
                        var level = new OrderBookLevel(JsonRead.Dec(c[1]), JsonRead.Dec(c[2]));
                        if (c[0].GetString() == "buy") bids.Add(level); else asks.Add(level);
                    }
                return Data("level2", new OrderBookUpdate
                {
                    Symbol = Sym(root),
                    Source = SourceName,
                    AsOfUtc = JsonRead.Time(root, "time") ?? receivedUtc,
                    ReceivedUtc = receivedUtc,
                    IsSnapshot = false,
                    Bids = bids,
                    Asks = asks,
                }, root);
            }
            default:
                return ParsedWsMessage.Of(WsMessageKind.Ignored) with { Channel = type };
        }
    }

    private static ParsedWsMessage Data(string channel, MarketEvent e, JsonElement root)
        => new(WsMessageKind.Data, [e]) { Channel = channel, Sequence = JsonRead.Long(root, "sequence") };

    private static string Sym(JsonElement root) => SymbolHelper.FromDashed(JsonRead.Str(root, "product_id")!);

    private QuoteTick ParseTicker(JsonElement t, DateTimeOffset received)
    {
        var open = JsonRead.DecOrNull(t, "open_24h");
        var last = JsonRead.DecOrNull(t, "price");
        return new QuoteTick
        {
            Symbol = Sym(t),
            Source = SourceName,
            AsOfUtc = JsonRead.Time(t, "time") ?? received,
            ReceivedUtc = received,
            Bid = JsonRead.DecOrNull(t, "best_bid"),
            BidSize = JsonRead.DecOrNull(t, "best_bid_size"),
            Ask = JsonRead.DecOrNull(t, "best_ask"),
            AskSize = JsonRead.DecOrNull(t, "best_ask_size"),
            Last = last,
            Volume24h = JsonRead.DecOrNull(t, "volume_24h"),
            High24h = JsonRead.DecOrNull(t, "high_24h"),
            Low24h = JsonRead.DecOrNull(t, "low_24h"),
            ChangePercent = open is > 0 && last is { } l ? Math.Round((l - open.Value) / open.Value * 100m, 4) : null,
        };
    }

    private TradeTick ParseMatch(JsonElement t, DateTimeOffset received) => new()
    {
        Symbol = Sym(t),
        Source = SourceName,
        AsOfUtc = JsonRead.Time(t, "time") ?? received,
        ReceivedUtc = received,
        Price = JsonRead.Dec(t.GetProperty("price")),
        Quantity = JsonRead.Dec(t.GetProperty("size")),
        // Coinbase meldet die Seite des Makers – der Aggressor ist die Gegenseite.
        AggressorSide = JsonRead.Str(t, "side") switch { "buy" => OrderSide.Sell, "sell" => OrderSide.Buy, _ => null },
        TradeId = JsonRead.Str(t, "trade_id"),
    };

    internal static IReadOnlyList<OrderBookLevel> Levels(JsonElement b, string name)
    {
        if (!b.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return [];
        return arr.EnumerateArray().Select(l => new OrderBookLevel(JsonRead.Dec(l[0]), JsonRead.Dec(l[1]))).ToList();
    }

    public static string BuildSubscribe(IEnumerable<string> symbols, bool level2, string type = "subscribe")
    {
        var channels = new List<string> { "ticker", "matches", "heartbeat" };
        if (level2) channels.Add("level2_batch");
        return JsonSerializer.Serialize(new { type, product_ids = symbols.Select(SymbolHelper.ToDashed).ToArray(), channels });
    }
}
