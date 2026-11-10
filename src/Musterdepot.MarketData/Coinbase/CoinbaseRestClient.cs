using System.Globalization;
using System.Text.Json;
using Musterdepot.Core.Domain;
using Musterdepot.MarketData.Infrastructure;

namespace Musterdepot.MarketData.Coinbase;

/// <summary>REST https://api.exchange.coinbase.com: Kerzen (max. 300 je Abruf, stückweise), Produkte, Orderbuch.</summary>
public sealed class CoinbaseRestClient(HttpClient http, string baseUrl = "https://api.exchange.coinbase.com")
{
    public const int MaxCandlesPerRequest = 300;
    private static readonly int[] Granularities = [60, 300, 900, 3600, 21600, 86400];

    public TimeSpan PageDelay { get; init; } = TimeSpan.FromMilliseconds(150);
    public int MaxPages { get; init; } = 500;

    private string Url(string pathAndQuery) => baseUrl.TrimEnd('/') + pathAndQuery;

    /// <summary>Native Granularität für einen Zeitrahmen; M30/H4/W1 werden aus der nächstkleineren gebildet.</summary>
    public static (int Seconds, Timeframe Native) GranularityFor(Timeframe tf) => tf switch
    {
        Timeframe.M1 => (60, Timeframe.M1),
        Timeframe.M5 => (300, Timeframe.M5),
        Timeframe.M15 => (900, Timeframe.M15),
        Timeframe.M30 => (900, Timeframe.M15),
        Timeframe.H1 => (3600, Timeframe.H1),
        Timeframe.H4 => (3600, Timeframe.H1),
        Timeframe.D1 => (86400, Timeframe.D1),
        Timeframe.W1 => (86400, Timeframe.D1),
        _ => throw new ArgumentOutOfRangeException(nameof(tf))
    };

    public async Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, Timeframe tf, DateTimeOffset fromUtc, DateTimeOffset toUtc, DateTimeOffset nowUtc, CancellationToken ct)
    {
        var (gran, native) = GranularityFor(tf);
        var step = TimeSpan.FromSeconds(gran * (double)MaxCandlesPerRequest);
        var product = SymbolHelper.ToDashed(symbol);
        var all = new Dictionary<DateTimeOffset, Candle>();
        var start = native.Floor(fromUtc);
        for (int page = 0; page < MaxPages && start < toUtc; page++)
        {
            var end = start + step;
            if (end > toUtc) end = toUtc;
            var q = $"/products/{product}/candles?granularity={gran}&start={Iso(start)}&end={Iso(end)}";
            var json = await http.GetStringAsync(Url(q), ct).ConfigureAwait(false);
            foreach (var c in ParseCandles(json, symbol, native, nowUtc)) all[c.OpenTimeUtc] = c;
            start = end;
            if (start < toUtc && PageDelay > TimeSpan.Zero) await Task.Delay(PageDelay, ct).ConfigureAwait(false);
        }
        IReadOnlyList<Candle> result = all.Values.Where(c => c.OpenTimeUtc >= native.Floor(fromUtc) && c.OpenTimeUtc < toUtc).OrderBy(c => c.OpenTimeUtc).ToList();
        if (native != tf) result = TradeCandleAggregator.Resample(result, tf);
        return result;
    }

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public async Task<IReadOnlyList<Instrument>> GetProductsAsync(CancellationToken ct)
        => ParseProducts(await http.GetStringAsync(Url("/products"), ct).ConfigureAwait(false));

    public async Task<(IReadOnlyList<OrderBookLevel> Bids, IReadOnlyList<OrderBookLevel> Asks)> GetBookAsync(string symbol, CancellationToken ct)
    {
        var json = await http.GetStringAsync(Url($"/products/{SymbolHelper.ToDashed(symbol)}/book?level=2"), ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        return (CoinbaseMessageParser.Levels(doc.RootElement, "bids"), CoinbaseMessageParser.Levels(doc.RootElement, "asks"));
    }

    /// <summary>[time_s, low, high, open, close, volume], neueste zuerst.</summary>
    public static IReadOnlyList<Candle> ParseCandles(string json, string symbol, Timeframe tf, DateTimeOffset nowUtc)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object)
            throw new HttpRequestException($"Coinbase: {JsonRead.Str(root, "message")}");
        return root.EnumerateArray().Select(r =>
        {
            var open = JsonRead.FromUnixSeconds((long)JsonRead.Dec(r[0]));
            return new Candle
            {
                Symbol = symbol,
                Timeframe = tf,
                OpenTimeUtc = open,
                Low = JsonRead.Dec(r[1]),
                High = JsonRead.Dec(r[2]),
                Open = JsonRead.Dec(r[3]),
                Close = JsonRead.Dec(r[4]),
                Volume = JsonRead.Dec(r[5]),
                Source = "coinbase",
                IsClosed = open + tf.ToTimeSpan() <= nowUtc,
            };
        }).OrderBy(c => c.OpenTimeUtc).ToList();
    }

    public static IReadOnlyList<Instrument> ParseProducts(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<Instrument>();
        foreach (var p in doc.RootElement.EnumerateArray())
        {
            var id = JsonRead.Str(p, "id");
            if (id is null) continue;
            if (JsonRead.Bool(p, "trading_disabled") == true) continue;
            var status = JsonRead.Str(p, "status");
            if (status is not null && status != "online") continue;
            var b = JsonRead.Str(p, "base_currency") ?? id.Split('-')[0];
            var q = JsonRead.Str(p, "quote_currency") ?? id.Split('-')[^1];
            list.Add(new Instrument
            {
                Symbol = SymbolHelper.FromDashed(id),
                Name = $"{b} / {q}",
                AssetClass = AssetClass.Crypto,
                QuoteCurrency = q,
                BaseAsset = b,
                TickSize = JsonRead.DecOrNull(p, "quote_increment") ?? 0.01m,
                QuantityStep = JsonRead.DecOrNull(p, "base_increment") ?? 0.00000001m,
                MinQuantity = JsonRead.DecOrNull(p, "base_min_size") ?? 0m,
                PrimarySource = "coinbase",
                SourceSymbol = id,
            });
        }
        return list;
    }
}
