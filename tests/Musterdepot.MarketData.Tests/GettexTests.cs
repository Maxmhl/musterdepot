using System.IO.Compression;
using System.Text;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;
using Musterdepot.MarketData.Gettex;
using Musterdepot.MarketData.Infrastructure;

namespace Musterdepot.MarketData.Tests;

public class GettexParserTests
{
    private static readonly DateOnly Day = new(2026, 11, 9);
    private static DateTimeOffset Utc(int h, int m, int s, int ms = 0) => new(2026, 11, 9, h, m, s, ms, TimeSpan.Zero);

    [Fact]
    public void GermanSemicolon_DecimalComma_BerlinTime()
    {
        var r = new GettexCsvParser().Parse(Fixture.Read("gettex/posttrade_de_semicolon.csv"), Day);
        Assert.True(r.FormatRecognized);
        Assert.True(r.HasHeader);
        Assert.Equal(';', r.Delimiter);
        Assert.True(r.DecimalComma);
        Assert.Equal(4, r.Trades.Count);
        Assert.Equal(2, r.SkippedLines); // kaputte ISIN, leerer Preis
        var first = r.Trades[0];
        Assert.Equal("DE0007164600", first.Isin);
        Assert.Equal(182.40m, first.Price);
        Assert.Equal(50m, first.Volume);
        // 09:00:01,123456 MEZ (Winterzeit) = 08:00:01.123456 UTC
        Assert.Equal(Utc(8, 0, 1).AddTicks(1_234_560), first.TimeUtc);
        Assert.Equal(28.155m, r.Trades.Single(t => t.Isin == "DE0005557508").Price);
        Assert.Equal(1000m, r.Trades.Single(t => t.Isin == "DE0005557508").Volume);
        Assert.Equal(1182.50m, r.Trades[^1].Price); // Tausenderpunkt
    }

    [Fact]
    public void EnglishComma_WithBidAsk_IsoAndLocalTimes()
    {
        var r = new GettexCsvParser().Parse(Fixture.Read("gettex/posttrade_en_comma.csv"), Day);
        Assert.Equal(',', r.Delimiter);
        Assert.False(r.DecimalComma);
        Assert.Equal(3, r.Trades.Count);
        var allianz = r.Trades.Where(t => t.Isin == "DE0008404005").ToList();
        Assert.Equal(Utc(8, 0, 1).AddTicks(1_234_567), allianz[0].TimeUtc); // Nanosekunden gekürzt
        Assert.Equal(281.25m, allianz[0].Bid);
        Assert.Equal(281.35m, allianz[0].Ask);
        Assert.Equal(Utc(8, 30, 0), allianz[1].TimeUtc); // 09:30 Berlin
        var basf = r.Trades.Single(t => t.Isin == "DE000BASF111");
        Assert.Equal(45.62m, basf.Price);
        Assert.Null(basf.Bid);
    }

    [Fact]
    public void TabSeparated_GermanHeaders()
    {
        var r = new GettexCsvParser().Parse(Fixture.Read("gettex/posttrade_tab.csv"), Day);
        Assert.Equal('\t', r.Delimiter);
        Assert.Equal(2, r.Trades.Count);
        Assert.Equal(178.90m, r.Trades[0].Price);
        Assert.Equal(25m, r.Trades[0].Volume);
        Assert.Equal(Utc(9, 15, 30), r.Trades[0].TimeUtc);
    }

    [Fact]
    public void Headerless_ColumnsInferred()
    {
        var r = new GettexCsvParser().Parse(Fixture.Read("gettex/posttrade_headerless.csv"));
        Assert.True(r.FormatRecognized);
        Assert.False(r.HasHeader);
        Assert.Equal(["isin", "date", "time", "currency", "price", "volume"], r.Columns);
        Assert.Equal(3, r.Trades.Count);
        Assert.Equal(10.882m, r.Trades[0].Price);
        Assert.Equal(800m, r.Trades[0].Volume);
        Assert.Equal(Utc(6, 0, 20).AddTicks(5_515_840), r.Trades[0].TimeUtc);
    }

    [Fact]
    public void Headerless_ExplicitColumnMapping()
    {
        var parser = new GettexCsvParser(new GettexParserOptions { HeaderlessColumns = ["isin", "date", "time", "ignore", "price", "volume"] });
        var r = parser.Parse(Fixture.Read("gettex/posttrade_headerless.csv"));
        Assert.Equal(3, r.Trades.Count);
        Assert.Equal("EUR", r.Trades[0].Currency);
    }

    [Fact]
    public void FormatChange_IsDetected_NotThrown()
    {
        var parser = new GettexCsvParser();
        var a = parser.Parse(Fixture.Read("gettex/posttrade_de_semicolon.csv"), Day);
        Assert.False(a.FormatChanged);
        var a2 = parser.Parse(Fixture.Read("gettex/posttrade_de_semicolon.csv"), Day);
        Assert.False(a2.FormatChanged);
        var b = parser.Parse(Fixture.Read("gettex/posttrade_changed_format.csv"), Day);
        Assert.True(b.FormatChanged);
        Assert.True(b.FormatRecognized);
        Assert.Equal('|', b.Delimiter);
        Assert.Equal(a.Fingerprint, b.PreviousFingerprint);
        Assert.Contains("Dateiformat geändert", b.Warnings[0]);
        var t = Assert.Single(b.Trades);
        Assert.Equal(183.10m, t.Price);
        Assert.Equal(183.05m, t.Bid);
        Assert.Equal(183.15m, t.Ask);
    }

    [Fact]
    public void UnknownFormat_ReturnsNotRecognized()
    {
        var r = new GettexCsvParser().Parse(Fixture.Read("gettex/posttrade_unknown.csv"));
        Assert.False(r.FormatRecognized);
        Assert.Empty(r.Trades);
        Assert.NotEmpty(r.Warnings);
        Assert.Empty(new GettexCsvParser().Parse("").Trades);
    }

    [Fact]
    public void Gzip_IsDetected()
    {
        var gz = Gzip(Fixture.Read("gettex/posttrade_de_semicolon.csv"));
        var r = new GettexCsvParser().Parse(new MemoryStream(gz), Day);
        Assert.Equal(4, r.Trades.Count);
    }

    [Theory]
    [InlineData("182,40", true, "182.40")]
    [InlineData("1.182,50", true, "1182.50")]
    [InlineData("12.5", true, "12.5")]
    [InlineData("1.000", true, "1000")]
    [InlineData("1,182.50", false, "1182.50")]
    [InlineData("0.00012", false, "0.00012")]
    public void Numbers(string raw, bool comma, string expected)
    {
        Assert.True(GettexCsvParser.TryParseNumber(raw, comma, out var v));
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), v);
    }

    [Fact]
    public void BerlinTime_SummerAndWinter()
    {
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 7, 0, 0, TimeSpan.Zero), BerlinTime.ToUtc(new DateTime(2026, 7, 1, 9, 0, 0)));
        Assert.Equal(new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero), BerlinTime.ToUtc(new DateTime(2026, 1, 15, 9, 0, 0)));
        Assert.Equal(new DateTime(2026, 11, 9, 11, 0, 0), BerlinTime.FromUtc(new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void DaxList_HasValidIsins()
    {
        Assert.True(GettexInstruments.Dax.Count >= 30);
        Assert.All(GettexInstruments.Dax, i => Assert.True(SymbolHelper.IsIsin(i.Symbol), i.Symbol));
        Assert.False(SymbolHelper.IsIsin("DE0007164601"));
    }

    internal static byte[] Gzip(string text)
    {
        var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            gz.Write(Encoding.UTF8.GetBytes(text));
        return ms.ToArray();
    }
}

public class GettexSourceTests
{
    // 09.11.2026 11:20 Berlin = 10:20 UTC
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 10, 20, 0, TimeSpan.Zero);

    private static GettexSource Create(Dictionary<string, byte[]> files, ManualClock clock, out FakeHttpHandler handler)
    {
        var http = FakeHttpHandler.Client(req =>
        {
            var name = req.RequestUri!.Segments[^1];
            return files.TryGetValue(name, out var b) ? FakeHttpHandler.Bytes(b) : FakeHttpHandler.NotFound();
        }, out handler);
        var options = new GettexOptions
        {
            BaseUrl = "https://gettex.example/files/",
            FileNamePatterns = ["posttrade.{yyyyMMdd}.{HH}.{mm}.csv.gz"],
            Lookback = TimeSpan.FromHours(2),
        };
        return new GettexSource(options, http, clock);
    }

    [Fact]
    public void ExpectedFiles_FollowNamingSchemeInBerlinTime()
    {
        var src = Create([], new ManualClock(Now), out _);
        var files = src.GetExpectedFiles(Now);
        Assert.Equal("posttrade.20261109.09.15.csv.gz", files[0].FileName); // 08:15 UTC = 09:15 Berlin
        Assert.Equal("posttrade.20261109.11.00.csv.gz", files[^1].FileName); // Fenster 11:00–11:15 ist veröffentlicht
        Assert.Equal(8, files.Count);
    }

    [Fact]
    public async Task Poll_EmitsDelayedTradesAndQuote_ForSubscribedIsins()
    {
        var clock = new ManualClock(Now);
        var files = new Dictionary<string, byte[]>
        {
            ["posttrade.20261109.10.00.csv.gz"] = GettexParserTests.Gzip(Fixture.Read("gettex/posttrade_de_semicolon.csv")),
        };
        var src = Create(files, clock, out var handler);
        var log = new EventLog();
        src.EventReceived += log.Add;
        await src.SubscribeAsync(["DE0007164600", "BTC/EUR"], default);
        Assert.Equal(["DE0007164600"], src.Subscriptions);

        var n = await src.PollOnceAsync(default);
        Assert.Equal(4, n);
        Assert.Equal(ConnectionState.Connected, src.State);
        var trades = log.Of<TradeTick>();
        Assert.Equal(3, trades.Count);
        Assert.All(trades, t =>
        {
            Assert.Equal(15, t.DelayedMinutes);
            Assert.Equal(DataQuality.Delayed, t.Quality);
            Assert.False(t.IsRealtime);
            Assert.Equal("gettex", t.Source);
        });
        Assert.Equal(trades.OrderBy(t => t.AsOfUtc).Select(t => t.AsOfUtc), trades.Select(t => t.AsOfUtc));
        var quote = Assert.Single(log.Of<QuoteTick>());
        Assert.Equal(1182.50m, quote.Last);
        Assert.Equal(DataQuality.Delayed, quote.Quality);

        // zweiter Abruf: nichts Neues, keine Doppelungen
        Assert.Equal(0, await src.PollOnceAsync(default));
        Assert.Equal(3, log.Of<TradeTick>().Count);

        var candles = await src.GetCandlesAsync("DE0007164600", Timeframe.M1, null, null, default);
        Assert.Equal(2, candles.Count); // 09:00 (2 Trades) und 09:02 Berlin
        Assert.Equal(2, candles[0].TradeCount);
        Assert.Null(await src.GetOrderBookAsync("DE0007164600", 10, default));
    }

    [Fact]
    public async Task FormatChange_ReportedAsStatusMessage()
    {
        var clock = new ManualClock(Now);
        var files = new Dictionary<string, byte[]>
        {
            ["posttrade.20261109.10.00.csv.gz"] = GettexParserTests.Gzip(Fixture.Read("gettex/posttrade_de_semicolon.csv")),
            ["posttrade.20261109.10.15.csv.gz"] = Encoding.UTF8.GetBytes(Fixture.Read("gettex/posttrade_changed_format.csv")),
            ["posttrade.20261109.10.30.csv.gz"] = Encoding.UTF8.GetBytes(Fixture.Read("gettex/posttrade_unknown.csv")),
        };
        var src = Create(files, clock, out _);
        var log = new EventLog();
        src.EventReceived += log.Add;
        await src.PollOnceAsync(default);
        var status = log.Of<ConnectionStatusChanged>();
        Assert.Contains(status, s => s.Message?.Contains("Dateiformat geändert") == true);
        Assert.Contains(status, s => s.Message?.Contains("nicht erkannt") == true);
        Assert.NotEqual(ConnectionState.Failed, src.State);
    }

    [Fact]
    public async Task Search_FindsDaxByNameWknIsin()
    {
        var src = Create([], new ManualClock(Now), out _);
        Assert.Equal("DE0007164600", (await src.SearchSymbolsAsync("SAP", default))[0].Symbol);
        Assert.Equal("DE0005557508", (await src.SearchSymbolsAsync("555750", default))[0].Symbol);
        Assert.Equal("Siemens AG", (await src.SearchSymbolsAsync("DE0007236101", default))[0].Name);
        Assert.True(src.Supports("DE0007164600"));
        Assert.False(src.Supports("BTC/EUR"));
    }

    [Fact]
    public async Task Resume_PollsAndReportsGapWithCandles()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 9, 7, 50, 0, TimeSpan.Zero));
        var files = new Dictionary<string, byte[]>
        {
            ["posttrade.20261109.10.00.csv.gz"] = GettexParserTests.Gzip(Fixture.Read("gettex/posttrade_de_semicolon.csv")),
        };
        var src = Create(files, clock, out _);
        var log = new EventLog();
        src.EventReceived += log.Add;
        await src.SubscribeAsync(["DE0007164600"], default);
        await src.SuspendAsync(default);
        clock.Set(Now);
        await src.ResumeAsync(default);
        var gap = Assert.Single(log.Of<GapDetected>());
        Assert.Equal(2, gap.BackfilledCandles.Count);
        Assert.All(gap.BackfilledCandles, c => Assert.True(c.IsBackfill));
        await src.StopAsync(default);
    }
}
