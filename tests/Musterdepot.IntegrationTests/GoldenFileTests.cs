using Musterdepot.IntegrationTests.Infrastructure;
using Musterdepot.Recorder.Inspection;
using Xunit.Sdk;

namespace Musterdepot.IntegrationTests;

public class GoldenFileTests
{
    private sealed record Sample(string Name, decimal Amount, DateTimeOffset AtUtc);

    [Fact]
    public void Verify_FailsWhenMissing_WritesInUpdateMode_PassesWhenEqual_FailsOnDifference()
    {
        var dir = TestPaths.NewTempDirectory();
        try
        {
            var value = new Sample("Kauf", 0.10000000m, new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero));
            var check = new GoldenFile(dir, update: false);
            var missing = Assert.Throws<XunitException>(() => check.Verify("sub/probe", value));
            Assert.Contains("UPDATE_GOLDEN=1", missing.Message);
            Assert.True(File.Exists(Path.Combine(dir, "sub", "probe.actual.json")));

            new GoldenFile(dir, update: true).Verify("sub/probe", value);
            var text = File.ReadAllText(Path.Combine(dir, "sub", "probe.json"));
            Assert.Contains("\"amount\": 0.10000000", text); // decimal verlustfrei, camelCase
            Assert.DoesNotContain("\r", text);
            Assert.False(File.Exists(Path.Combine(dir, "sub", "probe.actual.json")));

            check.Verify("sub/probe", value);
            check.VerifyText("sub/probe", text.Replace("\n", "\r\n")); // Zeilenenden egal

            var changed = Assert.Throws<XunitException>(() => check.Verify("sub/probe", value with { Amount = 0.1m }));
            Assert.Contains("Zeile 3", changed.Message);
            Assert.Contains("0.10000000", changed.Message);
        }
        finally
        {
            TestPaths.TryDeleteDirectory(dir);
        }
    }

    public static TheoryData<string> Names() => ReplayFixture.Names();

    /// <summary>Golden-Statistik je Szenario: macht jede (auch gewollte) Änderung an den Testdaten im Diff sichtbar.</summary>
    [Theory, MemberData(nameof(Names))]
    public async Task FixtureStatistics_MatchGolden(string name)
    {
        var f = ReplayFixture.Load(name);
        var stats = await InspectCommand.AnalyzeAsync([f.ReplayPath], TimeSpan.FromSeconds(60));
        GoldenFile.Default.Verify($"fixture-stats/{name}", stats);
    }
}
