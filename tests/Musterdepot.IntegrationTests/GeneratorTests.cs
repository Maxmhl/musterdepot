using Musterdepot.IntegrationTests.Infrastructure;
using Musterdepot.Recorder.Fixtures;

namespace Musterdepot.IntegrationTests;

/// <summary>Die eingecheckten Fixtures entsprechen genau dem, was der Generator mit dem Standard-Seed erzeugt.</summary>
public class GeneratorTests
{
    [Fact]
    public async Task CheckedInFixtures_AreReproducibleFromGenerator()
    {
        var dir = TestPaths.NewTempDirectory();
        try
        {
            var written = await FixtureGenerator.WriteAllAsync(dir, FixtureGenerator.DefaultSeed);
            Assert.Equal(3, written.Count);
            foreach (var file in Directory.GetFiles(dir))
            {
                var checkedIn = Path.Combine(TestPaths.ReplaysDirectory, Path.GetFileName(file));
                Assert.True(File.Exists(checkedIn), $"{checkedIn} fehlt – Fixtures neu erzeugen (siehe docs/Tests.md).");
                var expected = ReadLines(checkedIn);
                var actual = ReadLines(file);
                Assert.True(expected.Count == actual.Count, $"{Path.GetFileName(file)}: {expected.Count} statt {actual.Count} Zeilen. Generator geändert? Fixtures neu erzeugen.");
                for (int i = 0; i < expected.Count; i++)
                    Assert.True(expected[i] == actual[i], $"{Path.GetFileName(file)} Zeile {i + 1} weicht ab. Generator geändert? Fixtures neu erzeugen.");
            }
        }
        finally
        {
            TestPaths.TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public void DifferentSeed_GivesDifferentData_SameSeed_SameData()
    {
        var a = FixtureGenerator.CreateQuiet(1);
        var b = FixtureGenerator.CreateQuiet(1);
        var c = FixtureGenerator.CreateQuiet(2);
        Assert.Equal(a.Events, b.Events, (x, y) => Musterdepot.Core.Serialization.JsonDefaults.SerializeMarketEvent(x) == Musterdepot.Core.Serialization.JsonDefaults.SerializeMarketEvent(y));
        Assert.NotEqual(
            a.Events.OfType<Musterdepot.Core.MarketData.TradeTick>().Select(t => t.Price),
            c.Events.OfType<Musterdepot.Core.MarketData.TradeTick>().Select(t => t.Price));
    }

    private static List<string> ReadLines(string path) =>
        File.ReadAllText(path).Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList();
}
