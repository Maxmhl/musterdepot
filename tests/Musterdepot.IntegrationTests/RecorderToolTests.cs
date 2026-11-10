using Musterdepot.Core.Abstractions;
using Musterdepot.Core.MarketData;
using Musterdepot.Core.Serialization;
using Musterdepot.IntegrationTests.Infrastructure;
using Musterdepot.Recorder.Cli;
using Musterdepot.Recorder.Fixtures;
using Musterdepot.Recorder.Inspection;
using Musterdepot.Recorder.Recording;

namespace Musterdepot.IntegrationTests;

/// <summary>Werkzeugteile des Recorders, die ohne Netz und ohne echte Marktdaten-Implementierung laufen.</summary>
public class RecorderToolTests
{
    [Fact]
    public void CliArgs_ParsesOptionsFlagsAndPositionals()
    {
        var a = CliArgs.Parse(["datei.jsonl", "--symbols", "BTC/EUR, ETH/EUR", "--minutes=5", "--force", "--out", "x.jsonl"], ["force"]);
        Assert.Equal(["datei.jsonl"], a.Positionals);
        Assert.Equal(["BTC/EUR", "ETH/EUR"], a.GetList("symbols"));
        Assert.Equal(5, a.GetInt("minutes", 0));
        Assert.True(a.Has("force"));
        Assert.Null(a.Get("force"));
        Assert.Equal("x.jsonl", a.Get("out"));
        Assert.Throws<CliException>(() => a.EnsureOnly("symbols", "minutes", "force"));
        Assert.Throws<CliException>(() => CliArgs.Parse(["--out"]));
        Assert.Throws<CliException>(() => CliArgs.Parse(["--minutes", "1", "--minutes", "2"]));
    }

    [Fact]
    public void TimeArg_ParsesIsoAndRelative()
    {
        var reference = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
        Assert.Equal(reference.AddMinutes(5), TimeArg.Parse("+5m").Resolve(reference));
        Assert.Equal(reference.AddSeconds(90), TimeArg.Parse("+90s").Resolve(reference));
        Assert.Equal(reference.AddHours(1), TimeArg.Parse("+1h").Resolve(reference));
        Assert.Equal(reference.AddMinutes(3), TimeArg.Parse("2026-11-02T10:03:00Z").Resolve(DateTimeOffset.MinValue));
        Assert.Equal(reference.AddMinutes(3), TimeArg.Parse("2026-11-02T10:03:00").Resolve(DateTimeOffset.MinValue)); // ohne Zone = UTC
        Assert.Throws<CliException>(() => TimeArg.Parse("+5x"));
        Assert.Throws<CliException>(() => TimeArg.Parse("gestern"));
    }

    [Fact]
    public void RecordSettings_MapSourceNames()
    {
        var s = RecordCommand.BuildSettings(["kraken", "Bitvavo"]);
        Assert.True(s.KrakenEnabled);
        Assert.True(s.BitvavoEnabled);
        Assert.False(s.CoinbaseEnabled);
        Assert.False(s.GettexEnabled);
        Assert.False(s.EcbEnabled);
        Assert.False(s.UseFakeData);
        Assert.True(RecordCommand.BuildSettings(["fake"]).UseFakeData);
        Assert.Throws<CliException>(() => RecordCommand.BuildSettings(["binance"]));
    }

    [Theory]
    [InlineData("rec/btc.jsonl.gz", "rec/btc-20261109-14.jsonl.gz")]
    [InlineData("rec/btc.jsonl", "rec/btc-20261109-14.jsonl")]
    [InlineData("rec/btc.dat", "rec/btc-20261109-14.dat")]
    public void Rotation_PathNamesContainTheHour(string basePath, string expected)
    {
        var hour = new DateTimeOffset(2026, 11, 9, 14, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, RotatingRecordingWriter.PathForHour(basePath, hour));
    }

    [Fact]
    public async Task RotatingWriter_StartsNewFileEveryHour_AndNeverAppendsToExistingGzip()
    {
        var dir = TestPaths.NewTempDirectory();
        try
        {
            var events = ReplayFixture.Load(FixtureGenerator.QuietName).Events.Take(30).ToList();
            var clock = new ManualClock(new DateTimeOffset(2026, 11, 9, 9, 59, 50, TimeSpan.Zero));
            var basePath = Path.Combine(dir, "rec.jsonl.gz");
            await using (var w = new RotatingRecordingWriter(basePath, rotateHourly: true, clock))
            {
                for (int i = 0; i < events.Count; i++)
                {
                    await w.WriteAsync(events[i]);
                    clock.Advance(TimeSpan.FromSeconds(1)); // 10 Ereignisse vor 10:00, 20 danach
                }
                Assert.Equal(2, w.Files.Count);
            }
            var first = MarketEventJsonl.ReadAll(Path.Combine(dir, "rec-20261109-09.jsonl.gz"));
            var second = MarketEventJsonl.ReadAll(Path.Combine(dir, "rec-20261109-10.jsonl.gz"));
            Assert.Equal(10, first.Count);
            Assert.Equal(20, second.Count);
            Assert.Equal(events.Select(JsonDefaults.SerializeMarketEvent), first.Concat(second).Select(JsonDefaults.SerializeMarketEvent));

            // Neustart in derselben Stunde: neue Datei mit Zähler statt Anhängen an das gzip.
            clock.Set(new DateTimeOffset(2026, 11, 9, 10, 30, 0, TimeSpan.Zero));
            await using (var w = new RotatingRecordingWriter(basePath, rotateHourly: true, clock))
            {
                await w.WriteAsync(events[0]);
                Assert.EndsWith("rec-20261109-10-2.jsonl.gz", w.Files.Single());
            }
        }
        finally
        {
            TestPaths.TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task Inspect_ReportsGapsAndCounts()
    {
        var f = ReplayFixture.Load(FixtureGenerator.GapName);
        var stats = await InspectCommand.AnalyzeAsync([f.ReplayPath], TimeSpan.FromSeconds(60));
        Assert.Equal(f.Events.Count, stats.Events);
        Assert.Equal(0, stats.ParseErrors);
        Assert.Equal(0, stats.AsOfOutOfOrder);
        Assert.Equal(0, stats.ReceivedOutOfOrder);
        Assert.Equal(7, stats.BackfilledCandles);
        Assert.Contains(stats.Gaps, g => g.Kind == "gap-detected" && g.Seconds == 420);
        Assert.DoesNotContain(stats.Gaps, g => g.Kind == "missing-candles");
        Assert.Equal(f.Events.Count(e => e is TradeTick), stats.ByType["trade"]);
        var text = InspectCommand.FormatText(stats, 20);
        Assert.Contains("gap-detected", text);
        Assert.Contains("BTC/EUR", text);
    }

    [Fact]
    public void Inspect_ClosedCandleAfterClosingTrade_IsNoRewind()
    {
        var minute = new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);
        var stats = new RecordingStats(TimeSpan.FromSeconds(60));
        stats.Add(new TradeTick { Symbol = "BTC/EUR", Source = "fake", AsOfUtc = minute.AddMinutes(1).AddMilliseconds(100), Price = 1m, Quantity = 1m });
        stats.Add(new CandleUpdate
        {
            Symbol = "BTC/EUR", Source = "fake", AsOfUtc = minute.AddMinutes(1),
            Candle = new Core.Domain.Candle { Symbol = "BTC/EUR", Timeframe = Core.Domain.Timeframe.M1, OpenTimeUtc = minute, Open = 1, High = 1, Low = 1, Close = 1 },
        });
        Assert.Equal(0, stats.AsOfOutOfOrder);
        stats.Add(new QuoteTick { Symbol = "BTC/EUR", Source = "fake", AsOfUtc = minute, Last = 1m });
        Assert.Equal(1, stats.AsOfOutOfOrder);
    }

    [Fact]
    public async Task Inspect_ToleratesBrokenLinesAndTruncatedGzip()
    {
        var dir = TestPaths.NewTempDirectory();
        try
        {
            var events = ReplayFixture.Load(FixtureGenerator.QuietName).Events.Take(200).ToList();
            var plain = Path.Combine(dir, "broken.jsonl");
            var lines = events.Select(JsonDefaults.SerializeMarketEvent).ToList();
            lines.Insert(5, "{kaputt");
            await File.WriteAllLinesAsync(plain, lines);
            var s1 = await InspectCommand.AnalyzeAsync([plain], TimeSpan.FromSeconds(60));
            Assert.Equal(200, s1.Events);
            Assert.Equal(1, s1.ParseErrors);
            Assert.False(s1.Truncated);

            var gz = Path.Combine(dir, "cut.jsonl.gz");
            await MarketEventJsonl.WriteAsync(gz, events);
            var bytes = await File.ReadAllBytesAsync(gz);
            await File.WriteAllBytesAsync(gz, bytes[..(bytes.Length * 2 / 3)]);
            var s2 = await InspectCommand.AnalyzeAsync([gz], TimeSpan.FromSeconds(60));
            Assert.True(s2.Events > 0 && s2.Events < 200);
            Assert.True(s2.Truncated);
        }
        finally
        {
            TestPaths.TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task Slice_CutsTimeWindowAndSymbols()
    {
        var dir = TestPaths.NewTempDirectory();
        try
        {
            var f = ReplayFixture.Load(FixtureGenerator.QuietName);
            var output = Path.Combine(dir, "slice.jsonl.gz");
            var from = new DateTimeOffset(2026, 11, 2, 10, 5, 0, TimeSpan.Zero);
            var to = from.AddMinutes(2);
            var (read, written, errors) = await SliceCommand.SliceAsync(new SliceCommand.Options(
                f.ReplayPath, output, TimeArg.Parse("+5m"), TimeArg.Parse("2026-11-02T10:07:00Z"), ["BTC/EUR"], ByReceived: false, Force: false));
            Assert.Equal(f.Events.Count, read);
            Assert.Equal(0, errors);
            var sliced = MarketEventJsonl.ReadAll(output);
            Assert.Equal(written, sliced.Count);
            Assert.Equal(f.Events.Count(e => e.AsOfUtc >= from && e.AsOfUtc < to), sliced.Count);
            Assert.All(sliced, e => Assert.InRange(e.AsOfUtc, from, to.AddTicks(-1)));
            Assert.Equal(2, sliced.OfType<CandleUpdate>().Count());

            // Ohne --force wird nichts überschrieben.
            await Assert.ThrowsAsync<CliException>(() => SliceCommand.SliceAsync(new SliceCommand.Options(
                f.ReplayPath, output, null, null, [], false, Force: false)));
            var (_, all, _) = await SliceCommand.SliceAsync(new SliceCommand.Options(f.ReplayPath, output, null, null, ["ETH/EUR"], false, Force: true));
            Assert.Equal(f.Events.Count(e => e.Symbol == "*"), all); // nur Status-Ereignisse bleiben
        }
        finally
        {
            TestPaths.TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task Cli_PrintsUsageAndRejectsUnknownCommands()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(0, await RecorderCli.RunAsync(["--help"], output, error, CancellationToken.None));
        Assert.Contains("generate-fixtures", output.ToString());
        Assert.Equal(1, await RecorderCli.RunAsync(["tanzen"], output, error, CancellationToken.None));
        Assert.Contains("Unbekannter Unterbefehl", error.ToString());
        Assert.Equal(1, await RecorderCli.RunAsync(["record", "--minutes", "1"], output, error, CancellationToken.None));
        Assert.Contains("--symbols", error.ToString());
    }

    [Fact]
    public async Task Cli_InspectJsonWorksOnFixture()
    {
        var output = new StringWriter();
        var f = ReplayFixture.Load(FixtureGenerator.DelayedName);
        Assert.Equal(0, await RecorderCli.RunAsync(["inspect", f.ReplayPath, "--json"], output, new StringWriter(), CancellationToken.None));
        Assert.Contains("\"DE0007164600\"", output.ToString());
    }
}
