using Musterdepot.Core.MarketData;
using Musterdepot.Core.Serialization;
using Musterdepot.IntegrationTests.Infrastructure;
using Musterdepot.Recorder.Cli;
using Musterdepot.Recorder.Inspection;

namespace Musterdepot.IntegrationTests;

/// <summary><c>record --sources fake</c>: die ganze Kette DI → Hub → Fake-Quelle → Aufzeichnung ohne Netz.</summary>
public class RecorderFakeSourceTests
{
    [Fact]
    public async Task Record_with_fake_source_writes_readable_recording()
    {
        var dir = TestPaths.NewTempDirectory("musterdepot-rec");
        try
        {
            var path = Path.Combine(dir, "fake.jsonl.gz");
            var output = new StringWriter();
            var error = new StringWriter();
            var exit = await RecorderCli.RunAsync(
                ["record", "--symbols", "BTC/EUR", "--seconds", "3", "--sources", "fake", "--out", path, "--no-rotate"],
                output, error, CancellationToken.None);
            Assert.True(exit == 0, error.ToString());

            var events = MarketEventJsonl.ReadAll(path);
            Assert.NotEmpty(events);
            Assert.All(events, e => Assert.Contains(e.Symbol, new[] { "BTC/EUR", "*" }));
            Assert.Contains(events, e => e is TradeTick);
            Assert.Contains(events, e => e is OrderBookUpdate);
            Assert.All(events, e => Assert.Equal("fake", e.Source));
            Assert.All(events, e => Assert.NotEqual(default, e.ReceivedUtc));

            var stats = await InspectCommand.AnalyzeAsync([path], TimeSpan.FromSeconds(60));
            Assert.Equal(events.Count, stats.Events);
            Assert.Equal(0, stats.ParseErrors);
            Assert.Equal(0, stats.AsOfOutOfOrder);
        }
        finally
        {
            TestPaths.TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task Record_stops_cleanly_on_cancellation_and_rotates_hourly_by_default()
    {
        var dir = TestPaths.NewTempDirectory("musterdepot-rec");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)); // wie Strg+C
            var exit = await RecorderCli.RunAsync(
                ["record", "--symbols", "BTC/EUR,ETH/EUR", "--sources", "fake", "--out", Path.Combine(dir, "rec.jsonl.gz")],
                new StringWriter(), new StringWriter(), cts.Token);
            Assert.Equal(0, exit);
            var files = Directory.GetFiles(dir, "rec-*.jsonl.gz");
            Assert.NotEmpty(files); // Stundendatei rec-yyyyMMdd-HH.jsonl.gz (zwei, falls die Stunde wechselte)
            var events = files.SelectMany(MarketEventJsonl.ReadAll).ToList();
            Assert.Contains(events, e => e.Symbol == "BTC/EUR");
            Assert.All(events, e => Assert.Contains(e.Symbol, new[] { "BTC/EUR", "ETH/EUR", "*" }));
            Assert.All(events, e => Assert.Equal(e.ToString(), JsonDefaults.DeserializeMarketEvent(JsonDefaults.SerializeMarketEvent(e)).ToString()));
        }
        finally
        {
            TestPaths.TryDeleteDirectory(dir);
        }
    }
}
