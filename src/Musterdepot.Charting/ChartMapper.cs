using System.Globalization;
using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;

namespace Musterdepot.Charting;

/// <summary>Welche Indikatoren berechnet werden sollen.</summary>
public sealed record IndicatorOptions
{
    public bool EmaFast { get; init; } = true;
    public int EmaFastPeriod { get; init; } = 20;
    public bool EmaSlow { get; init; }
    public int EmaSlowPeriod { get; init; } = 50;
    public bool Vwap { get; init; }
    public bool Rsi { get; init; }
    public int RsiPeriod { get; init; } = 14;
    public bool Bollinger { get; init; }
    public int BollingerPeriod { get; init; } = 20;
    public decimal BollingerK { get; init; } = 2m;
}

/// <summary>Wandelt Kerzen und Indikatorwerte in Chart-DTOs um.</summary>
public static class ChartMapper
{
    public const string EmaFastColor = "#2962FF";
    public const string EmaSlowColor = "#FF6D00";
    public const string VwapColor = "#AB47BC";
    public const string RsiColor = "#7E57C2";
    public const string BollingerColor = "#26A69A";

    public static long ToUnixSeconds(DateTimeOffset t) => t.ToUnixTimeSeconds();

    /// <summary>
    /// Sortiert nach Zeit, entfernt Duplikate (die zuletzt gelieferte Kerze gewinnt) und mappt auf <see cref="ChartBar"/>.
    /// </summary>
    public static IReadOnlyList<Candle> Normalize(IEnumerable<Candle> candles)
    {
        var byTime = new SortedDictionary<long, Candle>();
        foreach (var c in candles) byTime[ToUnixSeconds(c.OpenTimeUtc)] = c;
        return [.. byTime.Values];
    }

    public static ChartBar ToBar(Candle c) =>
        new(ToUnixSeconds(c.OpenTimeUtc), c.Open, c.High, c.Low, c.Close, c.Volume, c.IsBackfill);

    public static IReadOnlyList<ChartBar> ToBars(IReadOnlyList<Candle> normalized)
    {
        var bars = new ChartBar[normalized.Count];
        for (int i = 0; i < bars.Length; i++) bars[i] = ToBar(normalized[i]);
        return bars;
    }

    /// <summary>Linien-Punkte aus Indikatorwerten; <c>null</c>-Werte werden ausgelassen.</summary>
    public static IReadOnlyList<ChartPoint> ToLine(IReadOnlyList<Candle> candles, IReadOnlyList<decimal?> values, int decimals = 8)
    {
        if (candles.Count != values.Count) throw new ArgumentException("Kerzen und Werte müssen gleich lang sein.");
        var list = new List<ChartPoint>(values.Count);
        for (int i = 0; i < values.Count; i++)
            if (values[i] is { } v) list.Add(new ChartPoint(ToUnixSeconds(candles[i].OpenTimeUtc), Math.Round(v, decimals)));
        return list;
    }

    /// <summary>Berechnet alle gewünschten Indikatoren über die (normalisierten) Kerzen.</summary>
    public static IReadOnlyList<IndicatorSeries> ComputeIndicators(IReadOnlyList<Candle> candles, IndicatorOptions options)
    {
        var closes = Indicators.Closes(candles);
        var list = new List<IndicatorSeries>();
        if (options.EmaFast)
            list.Add(Line($"ema{options.EmaFastPeriod}", $"EMA {options.EmaFastPeriod}", 0, EmaFastColor, ToLine(candles, Indicators.Ema(closes, options.EmaFastPeriod))));
        if (options.EmaSlow)
            list.Add(Line($"ema{options.EmaSlowPeriod}", $"EMA {options.EmaSlowPeriod}", 0, EmaSlowColor, ToLine(candles, Indicators.Ema(closes, options.EmaSlowPeriod))));
        if (options.Bollinger)
        {
            var bb = Indicators.Bollinger(closes, options.BollingerPeriod, options.BollingerK);
            var k = options.BollingerK.ToString(CultureInfo.InvariantCulture);
            list.Add(Line("bbUpper", $"BB {options.BollingerPeriod} {k} oben", 0, BollingerColor, ToLine(candles, bb.Upper)) with { LineWidth = 1 });
            list.Add(Line("bbMiddle", $"BB {options.BollingerPeriod} Mitte", 0, BollingerColor, ToLine(candles, bb.Middle)) with { LineWidth = 1 });
            list.Add(Line("bbLower", $"BB {options.BollingerPeriod} {k} unten", 0, BollingerColor, ToLine(candles, bb.Lower)) with { LineWidth = 1 });
        }
        if (options.Vwap)
            list.Add(Line("vwap", "VWAP", 0, VwapColor, ToLine(candles, Indicators.Vwap(candles))));
        if (options.Rsi)
            list.Add(Line($"rsi{options.RsiPeriod}", $"RSI {options.RsiPeriod}", 1, RsiColor, ToLine(candles, Indicators.Rsi(closes, options.RsiPeriod), 4)) with { Levels = [70m, 30m] });
        return list;
    }

    /// <summary>Letzter Punkt jeder Serie – für Live-Updates nach einem neuen Tick.</summary>
    public static IReadOnlyList<IndicatorPoint> LastPoints(IReadOnlyList<IndicatorSeries> series)
    {
        var list = new List<IndicatorPoint>(series.Count);
        foreach (var s in series)
            if (s.Data.Count > 0)
            {
                var p = s.Data[^1];
                list.Add(new IndicatorPoint(s.Id, p.Time, p.Value));
            }
        return list;
    }

    /// <summary>Lücke auf das Kerzenraster des Zeitrahmens legen.</summary>
    public static ChartGap ToGap(GapDetected gap, Timeframe timeframe, TimeZoneInfo? displayZone = null)
    {
        var start = timeframe.Floor(gap.GapStartUtc);
        var end = timeframe.Floor(gap.GapEndUtc);
        if (end < start) end = start;
        var zone = displayZone ?? TimeZoneInfo.Local;
        var s = TimeZoneInfo.ConvertTime(gap.GapStartUtc, zone);
        var e = TimeZoneInfo.ConvertTime(gap.GapEndUtc, zone);
        var label = s.Date == e.Date
            ? $"Lücke {s:HH\\:mm}–{e:HH\\:mm}"
            : $"Lücke {s:dd.MM. HH\\:mm}–{e:dd.MM. HH\\:mm}";
        return new ChartGap(ToUnixSeconds(start), ToUnixSeconds(end), label);
    }

    /// <summary>Sinnvolle Nachkommastellen für die Preisachse.</summary>
    public static int SuggestPriceDecimals(IReadOnlyList<Candle> candles, decimal? tickSize = null)
    {
        if (tickSize is > 0m)
        {
            int d = 0;
            var t = tickSize.Value;
            while (t != decimal.Truncate(t) && d < 8) { t *= 10m; d++; }
            return d;
        }
        if (candles.Count == 0) return 2;
        var p = Math.Abs(candles[^1].Close);
        return p switch
        {
            >= 1000m => 2,
            >= 1m => 2,
            >= 0.1m => 4,
            >= 0.001m => 6,
            _ => 8,
        };
    }

    private static IndicatorSeries Line(string id, string title, int pane, string color, IReadOnlyList<ChartPoint> data) =>
        new() { Id = id, Title = title, Pane = pane, Color = color, Data = data };
}
