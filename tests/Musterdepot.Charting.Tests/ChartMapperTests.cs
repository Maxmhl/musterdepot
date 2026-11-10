using System.Globalization;
using System.Text.Json;
using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;
using static Musterdepot.Charting.Tests.IndicatorTests;

namespace Musterdepot.Charting.Tests;

public class ChartMapperTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Normalize_sorts_and_deduplicates_last_wins()
    {
        var a = C(T0.AddMinutes(1), 1, 1, 1, 1, 1);
        var b = C(T0, 2, 2, 2, 2, 2);
        var a2 = a with { Close = 5m };
        var n = ChartMapper.Normalize([a, b, a2]);
        Assert.Equal(2, n.Count);
        Assert.Equal(T0, n[0].OpenTimeUtc);
        Assert.Equal(5m, n[1].Close);
    }

    [Fact]
    public void Bars_use_unix_seconds_and_keep_backfill_flag()
    {
        var bars = ChartMapper.ToBars([C(T0, 1, 2, 0.5m, 1.5m, 10) with { IsBackfill = true }]);
        Assert.Equal(T0.ToUnixTimeSeconds(), bars[0].Time);
        Assert.True(bars[0].Backfill);
        Assert.Equal(1.5m, bars[0].Close);
    }

    [Fact]
    public void Indicators_skip_null_values_and_rsi_goes_to_second_pane()
    {
        var candles = Enumerable.Range(0, 30).Select(i => C(T0.AddMinutes(i), 10 + i, 11 + i, 9 + i, 10 + i, 1)).ToList();
        var series = ChartMapper.ComputeIndicators(candles, new IndicatorOptions { EmaFast = true, EmaFastPeriod = 20, Rsi = true, Vwap = true });
        var ema = series.Single(s => s.Id == "ema20");
        Assert.Equal(11, ema.Data.Count);
        Assert.Equal(0, ema.Pane);
        var rsi = series.Single(s => s.Id == "rsi14");
        Assert.Equal(1, rsi.Pane);
        Assert.Equal([70m, 30m], rsi.Levels);
        Assert.Equal(16, rsi.Data.Count);
        Assert.Equal(30, series.Single(s => s.Id == "vwap").Data.Count);
        Assert.Equal(3, ChartMapper.LastPoints(series).Count);
    }

    [Fact]
    public void Messages_serialize_with_type_discriminator_and_camel_case()
    {
        var msg = new BarMessage(new ChartBar(100, 1.5m, 2m, 1m, 1.75m, 3m), [new IndicatorPoint("ema20", 100, 1.6m)]);
        using var doc = JsonDocument.Parse(msg.ToJson());
        var root = doc.RootElement;
        Assert.Equal("bar", root.GetProperty("type").GetString());
        Assert.Equal(1.75m, root.GetProperty("bar").GetProperty("close").GetDecimal());
        Assert.Equal("ema20", root.GetProperty("points")[0].GetProperty("id").GetString());

        var load = new LoadMessage { Symbol = "BTC/EUR", Timeframe = "M1", Bars = [] };
        using var doc2 = JsonDocument.Parse(load.ToJson());
        Assert.Equal("load", doc2.RootElement.GetProperty("type").GetString());
        Assert.False(doc2.RootElement.TryGetProperty("notice", out _));
    }

    [Fact]
    public void Gap_is_floored_to_timeframe_and_labelled()
    {
        var gap = new GapDetected
        {
            Symbol = "BTC/EUR",
            Source = "kraken",
            AsOfUtc = T0,
            GapStartUtc = T0.AddMinutes(2).AddSeconds(30),
            GapEndUtc = T0.AddMinutes(37).AddSeconds(5),
        };
        var g = ChartMapper.ToGap(gap, Timeframe.M5, TimeZoneInfo.Utc);
        Assert.Equal(T0.ToUnixTimeSeconds(), g.Start);
        Assert.Equal(T0.AddMinutes(35).ToUnixTimeSeconds(), g.End);
        Assert.Equal("Lücke 10:02–10:37", g.Label);
    }

    [Theory]
    [InlineData("0.01", 2)]
    [InlineData("0.00001", 5)]
    [InlineData("1", 0)]
    public void Price_decimals_follow_tick_size(string tick, int expected)
    {
        Assert.Equal(expected, ChartMapper.SuggestPriceDecimals([], decimal.Parse(tick, CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Live_series_updates_current_candle_and_starts_new_one()
    {
        var s = new LiveCandleSeries("BTC/EUR", Timeframe.M1, [C(T0, 100, 105, 99, 101, 2)]);
        var u1 = s.Apply(Trade(T0.AddSeconds(20), 107m, 0.5m));
        Assert.NotNull(u1);
        Assert.Equal(107m, u1!.High);
        Assert.Equal(107m, u1.Close);
        Assert.Equal(2.5m, u1.Volume);
        Assert.Single(s.Candles);

        var u2 = s.Apply(Trade(T0.AddMinutes(1).AddSeconds(1), 98m, 1m));
        Assert.Equal(2, s.Candles.Count);
        Assert.Equal(98m, u2!.Open);
        Assert.Equal(T0.AddMinutes(1), u2.OpenTimeUtc);

        Assert.Null(s.Apply(Trade(T0.AddSeconds(5), 1m, 1m)));               // zu alt
        Assert.Null(s.Apply(Trade(T0.AddMinutes(1), 1m, 1m) with { Symbol = "ETH/EUR" }));
    }

    [Fact]
    public void Live_series_replaces_candle_from_candle_update_and_merges_backfill()
    {
        var s = new LiveCandleSeries("BTC/EUR", Timeframe.M1, [C(T0, 100, 105, 99, 101, 2)]);
        var replaced = s.Apply(new CandleUpdate { Symbol = "BTC/EUR", Source = "kraken", AsOfUtc = T0, Candle = C(T0, 100, 110, 90, 95, 9) });
        Assert.Equal(95m, replaced!.Close);
        Assert.Single(s.Candles);

        s.Merge([C(T0.AddMinutes(-2), 1, 1, 1, 1, 1) with { IsBackfill = true }, C(T0.AddMinutes(-1), 1, 1, 1, 1, 1)]);
        Assert.Equal(3, s.Candles.Count);
        Assert.Equal(T0.AddMinutes(-2), s.Candles[0].OpenTimeUtc);
    }

    [Fact]
    public void Display_format_delay_badge_and_prices()
    {
        var de = CultureInfo.GetCultureInfo("de-DE");
        Assert.Equal("61.234,50", DisplayFormat.Price(61234.5m, de));
        Assert.Equal("0,1235", DisplayFormat.Price(0.123456m, de));
        Assert.Equal("0,00012346", DisplayFormat.Price(0.000123456m, de));
        Assert.Equal("+1,25 %", DisplayFormat.Percent(1.25m, de));
        Assert.Equal("−0,50 %", DisplayFormat.Percent(-0.5m, de));
        Assert.Equal("0,125", DisplayFormat.Quantity(0.125m, de));
        var delayed = new QuoteSnapshot { Symbol = "DE0007164600", Source = "gettex", AsOfUtc = T0, DelayedMinutes = 15, Quality = DataQuality.Delayed };
        Assert.Equal("verzögert 15 Min", DisplayFormat.DelayBadge(delayed));
        var rt = new QuoteSnapshot { Symbol = "BTC/EUR", Source = "kraken", AsOfUtc = T0, IsRealtime = true };
        Assert.Null(DisplayFormat.DelayBadge(rt));
    }

    private static TradeTick Trade(DateTimeOffset t, decimal price, decimal qty) => new()
    {
        Symbol = "BTC/EUR",
        Source = "kraken",
        AsOfUtc = t,
        Price = price,
        Quantity = qty,
    };
}
