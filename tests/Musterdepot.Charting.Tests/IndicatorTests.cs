using Musterdepot.Core.Domain;

namespace Musterdepot.Charting.Tests;

public class IndicatorTests
{
    // Referenzdaten: StockCharts "ChartSchool" – EMA (10 Tage, Intel) und RSI (14, Wilder).
    private static readonly decimal[] EmaInput =
    [
        22.27m, 22.19m, 22.08m, 22.17m, 22.18m, 22.13m, 22.23m, 22.43m, 22.24m, 22.29m,
        22.15m, 22.39m, 22.38m, 22.61m, 23.36m, 24.05m, 23.75m, 23.83m, 23.95m, 23.63m,
        23.82m, 23.87m, 23.65m, 23.19m, 23.10m, 23.33m, 22.68m, 23.10m, 22.40m, 22.17m,
    ];

    private static readonly decimal[] EmaExpected =
    [
        22.22m, 22.21m, 22.24m, 22.27m, 22.33m, 22.52m, 22.80m, 22.97m, 23.13m, 23.28m, 23.34m,
        23.43m, 23.51m, 23.53m, 23.47m, 23.40m, 23.39m, 23.26m, 23.23m, 23.08m, 22.92m,
    ];

    private static readonly decimal[] RsiInput =
    [
        44.34m, 44.09m, 44.15m, 43.61m, 44.33m, 44.83m, 45.10m, 45.42m, 45.84m, 46.08m, 45.89m,
        46.03m, 45.61m, 46.28m, 46.28m, 46.00m, 46.03m, 46.41m, 46.22m, 45.64m, 46.21m, 46.25m,
        45.71m, 46.45m, 45.78m, 45.35m, 44.03m, 44.18m, 44.22m, 44.57m, 43.42m, 42.66m, 43.13m,
    ];

    private static readonly decimal[] RsiExpected =
    [
        70.46m, 66.25m, 66.48m, 69.35m, 66.29m, 57.92m, 62.88m, 63.21m, 56.01m, 62.34m,
        54.67m, 50.39m, 40.02m, 41.49m, 41.90m, 45.50m, 37.32m, 33.09m, 37.79m,
    ];

    [Fact]
    public void Sma_matches_hand_calculation()
    {
        var r = Indicators.Sma([1m, 2m, 3m, 4m, 5m], 3);
        Assert.Equal([null, null, 2m, 3m, 4m], r);
    }

    [Fact]
    public void Ema_matches_stockcharts_reference()
    {
        var r = Indicators.Ema(EmaInput, 10);
        for (int i = 0; i < 9; i++) Assert.Null(r[i]);
        for (int i = 9; i < EmaInput.Length; i++)
            Assert.Equal(EmaExpected[i - 9], Math.Round(r[i]!.Value, 2));
    }

    [Fact]
    public void Ema_with_too_little_data_is_empty()
    {
        Assert.All(Indicators.Ema([1m, 2m], 5), v => Assert.Null(v));
    }

    [Fact]
    public void Rsi_matches_wilder_reference()
    {
        var r = Indicators.Rsi(RsiInput, 14);
        for (int i = 0; i < 14; i++) Assert.Null(r[i]);
        for (int i = 14; i < RsiInput.Length; i++)
            Assert.Equal(RsiExpected[i - 14], Math.Round(r[i]!.Value, 2));
    }

    [Fact]
    public void Rsi_is_100_when_only_gains_and_50_when_flat()
    {
        var up = Indicators.Rsi([1m, 2m, 3m, 4m, 5m], 3);
        Assert.Equal(100m, up[^1]);
        var flat = Indicators.Rsi([5m, 5m, 5m, 5m], 3);
        Assert.Equal(50m, flat[^1]);
    }

    [Fact]
    public void Bollinger_uses_population_standard_deviation()
    {
        // Mittelwert 5, Standardabweichung (Grundgesamtheit) 2.
        var bb = Indicators.Bollinger([2m, 4m, 4m, 4m, 5m, 5m, 7m, 9m], 8, 2m);
        Assert.Equal(5m, bb.Middle[^1]);
        Assert.Equal(9m, bb.Upper[^1]);
        Assert.Equal(1m, bb.Lower[^1]);
        Assert.Null(bb.Upper[6]);
    }

    [Fact]
    public void Bollinger_matches_reference_on_longer_series()
    {
        var bb = Indicators.Bollinger(EmaInput, 20, 2m);
        Assert.Equal(22.7155m, bb.Middle[19]);
        Assert.Equal(24.126053m, Math.Round(bb.Upper[19]!.Value, 6));
        Assert.Equal(21.304947m, Math.Round(bb.Lower[19]!.Value, 6));
        Assert.Equal(24.435466m, Math.Round(bb.Upper[29]!.Value, 6));
    }

    [Fact]
    public void Vwap_accumulates_per_utc_day_and_resets()
    {
        var d1 = new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);
        var candles = new[]
        {
            C(d1, 9m, 10m, 8m, 9m, 100m),            // typisch 9
            C(d1.AddMinutes(1), 9m, 12m, 10m, 11m, 300m), // typisch 11 → (900+3300)/400 = 10,5
            C(d1.AddMinutes(2), 11m, 11m, 11m, 11m, 0m),  // ohne Volumen: unverändert
            C(d1.AddDays(1), 20m, 21m, 19m, 20m, 50m),    // neuer Tag: 20
        };
        var v = Indicators.Vwap(candles);
        Assert.Equal(9m, v[0]);
        Assert.Equal(10.5m, v[1]);
        Assert.Equal(10.5m, v[2]);
        Assert.Equal(20m, v[3]);
    }

    [Fact]
    public void Vwap_without_volume_is_null()
    {
        var t = new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);
        Assert.Null(Indicators.Vwap([C(t, 1m, 1m, 1m, 1m, 0m)])[0]);
    }

    [Theory]
    [InlineData("2", "1.41421356237309504880")]
    [InlineData("144", "12")]
    [InlineData("0.0001", "0.01")]
    public void Sqrt_is_precise(string x, string expected)
    {
        var r = Indicators.Sqrt(decimal.Parse(x, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), Math.Round(r, 20));
    }

    internal static Candle C(DateTimeOffset t, decimal o, decimal h, decimal l, decimal c, decimal v, Timeframe tf = Timeframe.M1) => new()
    {
        Symbol = "BTC/EUR",
        Timeframe = tf,
        OpenTimeUtc = t,
        Open = o,
        High = h,
        Low = l,
        Close = c,
        Volume = v,
    };
}
