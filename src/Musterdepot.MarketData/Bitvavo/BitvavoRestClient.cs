using System.Text.Json;
using Musterdepot.Core.Domain;
using Musterdepot.MarketData.Infrastructure;

namespace Musterdepot.MarketData.Bitvavo;

/// <summary>REST https://api.bitvavo.com/v2: Kerzen (1440 je Abruf, Paging rückwärts), Märkte, Orderbuch.</summary>
public sealed class BitvavoRestClient(HttpClient http, string baseUrl = "https://api.bitvavo.com/v2")
{
    public const int MaxCandlesPerRequest = 1440;

    /// <summary>Pause zwischen Seiten (IP-Limit ohne Schlüssel).</summary>
    public TimeSpan PageDelay { get; init; } = TimeSpan.FromMilliseconds(100);
    /// <summary>Sicherheitsgrenze gegen Endlosschleifen.</summary>
    public int MaxPages { get; init; } = 500;

    private string Url(string pathAndQuery) => baseUrl.TrimEnd('/') + pathAndQuery;

    /// <summary>
    /// Lädt Kerzen im Bereich [from, to) seitenweise rückwärts: Bitvavo liefert die neuesten zuerst, also wird
    /// <c>end</c> nach jeder vollen Seite auf die älteste gelieferte Kerze gesetzt.
    /// </summary>
    public async Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, Timeframe tf, DateTimeOffset fromUtc, DateTimeOffset toUtc, DateTimeOffset nowUtc, CancellationToken ct)
    {
        var target = tf;
        var interval = BitvavoMessageParser.IntervalOf(tf);
        if (interval is null) { target = Timeframe.D1; interval = "1d"; }
        var market = SymbolHelper.ToDashed(symbol);
        var all = new Dictionary<DateTimeOffset, Candle>();
        var end = toUtc;
        var start = target.Floor(fromUtc);
        for (int page = 0; page < MaxPages && end > start; page++)
        {
            var q = $"/{market}/candles?interval={interval}&limit={MaxCandlesPerRequest}&start={start.ToUnixTimeMilliseconds()}&end={end.ToUnixTimeMilliseconds()}";
            var json = await http.GetStringAsync(Url(q), ct).ConfigureAwait(false);
            var batch = ParseCandles(json, symbol, target, nowUtc);
            if (batch.Count == 0) break;
            foreach (var c in batch) all[c.OpenTimeUtc] = c;
            var oldest = batch.Min(c => c.OpenTimeUtc);
            if (batch.Count < MaxCandlesPerRequest || oldest <= start || oldest >= end) break;
            end = oldest; // end ist exklusiv
            if (PageDelay > TimeSpan.Zero) await Task.Delay(PageDelay, ct).ConfigureAwait(false);
        }
        IReadOnlyList<Candle> result = all.Values.Where(c => c.OpenTimeUtc >= start && c.OpenTimeUtc < toUtc).OrderBy(c => c.OpenTimeUtc).ToList();
        if (target != tf) result = TradeCandleAggregator.Resample(result, tf);
        return result;
    }

    public async Task<IReadOnlyList<BitvavoMarket>> GetMarketsAsync(CancellationToken ct)
        => ParseMarkets(await http.GetStringAsync(Url("/markets"), ct).ConfigureAwait(false));

    public async Task<(IReadOnlyList<OrderBookLevel> Bids, IReadOnlyList<OrderBookLevel> Asks, long? Nonce)> GetBookAsync(string symbol, int depth, CancellationToken ct)
    {
        var json = await http.GetStringAsync(Url($"/{SymbolHelper.ToDashed(symbol)}/book?depth={depth}"), ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        return (BitvavoMessageParser.Levels(r, "bids"), BitvavoMessageParser.Levels(r, "asks"), JsonRead.Long(r, "nonce"));
    }

    // ---------- Parser ----------
    public static IReadOnlyList<Candle> ParseCandles(string json, string symbol, Timeframe tf, DateTimeOffset nowUtc)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && JsonRead.Str(root, "error") is { } err)
            throw new HttpRequestException($"Bitvavo: {err}");
        return root.EnumerateArray()
            .Select(row => BitvavoMessageParser.ParseCandleRow(row, symbol, tf, "bitvavo"))
            .Select(c => c with { IsClosed = c.OpenTimeUtc + tf.ToTimeSpan() <= nowUtc })
            .ToList();
    }

    public static IReadOnlyList<BitvavoMarket> ParseMarkets(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(m => new BitvavoMarket
        {
            Market = JsonRead.Str(m, "market")!,
            Base = JsonRead.Str(m, "base") ?? "",
            Quote = JsonRead.Str(m, "quote") ?? "",
            Status = JsonRead.Str(m, "status") ?? "trading",
            PricePrecision = (int?)JsonRead.Long(m, "pricePrecision"),
            MinOrderInBaseAsset = JsonRead.DecOrNull(m, "minOrderInBaseAsset"),
            QuantityDecimals = (int?)JsonRead.Long(m, "quantityDecimals"),
            TickSize = JsonRead.DecOrNull(m, "tickSize"),
        }).ToList();
    }
}

public sealed record BitvavoMarket
{
    public required string Market { get; init; }
    public required string Base { get; init; }
    public required string Quote { get; init; }
    public string Status { get; init; } = "trading";
    /// <summary>Bitvavo: signifikante Stellen.</summary>
    public int? PricePrecision { get; init; }
    public decimal? MinOrderInBaseAsset { get; init; }
    public int? QuantityDecimals { get; init; }
    public decimal? TickSize { get; init; }
    public string Symbol => SymbolHelper.FromDashed(Market);

    public Instrument ToInstrument()
    {
        decimal step = 0.00000001m;
        if (QuantityDecimals is { } qd) { step = 1m; for (int i = 0; i < qd; i++) step /= 10m; }
        return new Instrument
        {
            Symbol = Symbol,
            Name = $"{Base} / {Quote}",
            AssetClass = AssetClass.Crypto,
            QuoteCurrency = Quote,
            BaseAsset = Base,
            TickSize = TickSize ?? 0.01m,
            QuantityStep = step,
            MinQuantity = MinOrderInBaseAsset ?? 0m,
            PrimarySource = "bitvavo",
            SourceSymbol = Market,
        };
    }
}
