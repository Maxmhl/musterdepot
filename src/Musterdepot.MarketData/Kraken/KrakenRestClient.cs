using System.Globalization;
using System.Text.Json;
using Musterdepot.Core.Domain;
using Musterdepot.MarketData.Infrastructure;

namespace Musterdepot.MarketData.Kraken;

/// <summary>Ein Handelspaar aus /0/public/AssetPairs.</summary>
public sealed record KrakenPairInfo
{
    /// <summary>Programmsymbol (WS-v2-Name), z. B. "BTC/EUR".</summary>
    public required string Symbol { get; init; }
    /// <summary>REST-Name, z. B. "XBTEUR".</summary>
    public required string AltName { get; init; }
    /// <summary>Interner Schlüssel, z. B. "XXBTZEUR".</summary>
    public required string Key { get; init; }
    public required string Base { get; init; }
    public required string Quote { get; init; }
    public int PricePrecision { get; init; }
    public int QtyPrecision { get; init; }
    public decimal? TickSize { get; init; }
    public decimal? OrderMin { get; init; }
    public string Status { get; init; } = "online";

    public Instrument ToInstrument() => new()
    {
        Symbol = Symbol,
        Name = $"{Base} / {Quote}",
        AssetClass = AssetClass.Crypto,
        QuoteCurrency = Quote,
        BaseAsset = Base,
        TickSize = TickSize ?? Pow10(-PricePrecision),
        QuantityStep = Pow10(-QtyPrecision),
        MinQuantity = OrderMin ?? 0m,
        PrimarySource = "kraken",
        SourceSymbol = Symbol,
    };

    private static decimal Pow10(int e)
    {
        decimal r = 1m;
        for (int i = 0; i < -e; i++) r /= 10m;
        return r;
    }
}

/// <summary>REST-Zugriff auf https://api.kraken.com/0/public (OHLC max. 720 Kerzen, AssetPairs, Depth).</summary>
public sealed class KrakenRestClient(HttpClient http, string baseUrl = "https://api.kraken.com")
{
    public const int MaxOhlcCandles = 720;

    private string Url(string pathAndQuery) => baseUrl.TrimEnd('/') + pathAndQuery;

    public async Task<IReadOnlyList<KrakenPairInfo>> GetAssetPairsAsync(CancellationToken ct)
    {
        var json = await http.GetStringAsync(Url("/0/public/AssetPairs"), ct).ConfigureAwait(false);
        return ParseAssetPairs(json);
    }

    public async Task<IReadOnlyList<Candle>> GetOhlcAsync(string symbol, string restPair, Timeframe tf, DateTimeOffset? sinceUtc, DateTimeOffset nowUtc, CancellationToken ct)
    {
        var minutes = (int)tf.ToTimeSpan().TotalMinutes;
        var q = $"/0/public/OHLC?pair={Uri.EscapeDataString(restPair)}&interval={minutes}";
        if (sinceUtc is { } s) q += "&since=" + (s.ToUnixTimeSeconds() - 1).ToString(CultureInfo.InvariantCulture);
        var json = await http.GetStringAsync(Url(q), ct).ConfigureAwait(false);
        return ParseOhlc(json, symbol, tf, nowUtc);
    }

    public async Task<(IReadOnlyList<OrderBookLevel> Bids, IReadOnlyList<OrderBookLevel> Asks)> GetDepthAsync(string restPair, int count, CancellationToken ct)
    {
        var json = await http.GetStringAsync(Url($"/0/public/Depth?pair={Uri.EscapeDataString(restPair)}&count={count}"), ct).ConfigureAwait(false);
        return ParseDepth(json);
    }

    // ---------- Parser (öffentlich für Tests) ----------

    private static JsonElement Result(JsonDocument doc)
    {
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Array && err.GetArrayLength() > 0)
            throw new KrakenApiException(string.Join("; ", err.EnumerateArray().Select(e => e.GetString())));
        return root.GetProperty("result");
    }

    public static IReadOnlyList<KrakenPairInfo> ParseAssetPairs(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = Result(doc);
        var list = new List<KrakenPairInfo>();
        foreach (var p in result.EnumerateObject())
        {
            var v = p.Value;
            var wsname = JsonRead.Str(v, "wsname");
            var altname = JsonRead.Str(v, "altname") ?? p.Name;
            string b, q;
            if (wsname is not null && SymbolHelper.TrySplitPair(wsname, out var wb, out var wq))
            {
                b = SymbolHelper.NormalizeKrakenAsset(wb);
                q = SymbolHelper.NormalizeKrakenAsset(wq);
            }
            else
            {
                b = SymbolHelper.NormalizeKrakenAsset(JsonRead.Str(v, "base") ?? "");
                q = SymbolHelper.NormalizeKrakenAsset(JsonRead.Str(v, "quote") ?? "");
            }
            if (b.Length == 0 || q.Length == 0) continue;
            list.Add(new KrakenPairInfo
            {
                Symbol = $"{b}/{q}",
                AltName = altname,
                Key = p.Name,
                Base = b,
                Quote = q,
                PricePrecision = (int)(JsonRead.Long(v, "pair_decimals") ?? 2),
                QtyPrecision = (int)(JsonRead.Long(v, "lot_decimals") ?? 8),
                TickSize = JsonRead.DecOrNull(v, "tick_size"),
                OrderMin = JsonRead.DecOrNull(v, "ordermin"),
                Status = JsonRead.Str(v, "status") ?? "online",
            });
        }
        return list;
    }

    /// <summary>
    /// OHLC-Antwort: [time, open, high, low, close, vwap, volume, count]. Der letzte Eintrag ist die laufende Kerze.
    /// </summary>
    public static IReadOnlyList<Candle> ParseOhlc(string json, string symbol, Timeframe tf, DateTimeOffset nowUtc)
    {
        using var doc = JsonDocument.Parse(json);
        var result = Result(doc);
        var list = new List<Candle>();
        foreach (var p in result.EnumerateObject())
        {
            if (p.Name == "last" || p.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var row in p.Value.EnumerateArray())
            {
                var open = JsonRead.FromUnixSecondsDecimal(JsonRead.Dec(row[0]));
                list.Add(new Candle
                {
                    Symbol = symbol,
                    Timeframe = tf,
                    OpenTimeUtc = open,
                    Open = JsonRead.Dec(row[1]),
                    High = JsonRead.Dec(row[2]),
                    Low = JsonRead.Dec(row[3]),
                    Close = JsonRead.Dec(row[4]),
                    Volume = JsonRead.Dec(row[6]),
                    TradeCount = row.GetArrayLength() > 7 ? (int)JsonRead.Dec(row[7]) : 0,
                    Source = "kraken",
                    IsClosed = open + tf.ToTimeSpan() <= nowUtc,
                });
            }
        }
        return list.OrderBy(c => c.OpenTimeUtc).ToList();
    }

    public static (IReadOnlyList<OrderBookLevel> Bids, IReadOnlyList<OrderBookLevel> Asks) ParseDepth(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = Result(doc);
        foreach (var p in result.EnumerateObject())
        {
            var bids = p.Value.GetProperty("bids").EnumerateArray().Select(r => new OrderBookLevel(JsonRead.Dec(r[0]), JsonRead.Dec(r[1]))).ToList();
            var asks = p.Value.GetProperty("asks").EnumerateArray().Select(r => new OrderBookLevel(JsonRead.Dec(r[0]), JsonRead.Dec(r[1]))).ToList();
            return (bids, asks);
        }
        return ([], []);
    }
}

public sealed class KrakenApiException(string message) : Exception(message);
