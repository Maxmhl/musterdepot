using Musterdepot.Core.MarketData;
using Musterdepot.Core.Serialization;
using Musterdepot.Recorder.Fixtures;
using Musterdepot.Recorder.Recording;

namespace Musterdepot.IntegrationTests.Infrastructure;

/// <summary>Ein Schritt auf der gemeinsamen Zeitachse aus Marktdaten und Orderskript.</summary>
public abstract record ReplayStep(DateTimeOffset AtUtc);
public sealed record MarketReplayStep(DateTimeOffset AtUtc, MarketEvent Event) : ReplayStep(AtUtc);
public sealed record OrderReplayStep(DateTimeOffset AtUtc, int Index, OrderScriptStep Step) : ReplayStep(AtUtc);

/// <summary>
/// Lädt ein Replay-Szenario: <c>tests/data/replays/&lt;name&gt;.orders.json</c> (Orderskript) und die darin genannte
/// Aufzeichnung (<c>.jsonl</c> oder <c>.jsonl.gz</c>, <see cref="MarketEventJsonl"/>).
/// <para>Runde 2 (Golden Replay): <c>foreach (var s in fixture.Timeline())</c> → Uhr auf <c>s.AtUtc</c>, bei
/// <see cref="MarketReplayStep"/> <c>engine.OnMarketEvent</c>, bei <see cref="OrderReplayStep"/> <c>engine.Execute(step.ToEngineRequest(...))</c>
/// bzw. <c>ICommandBus.SendAsync(step.ToCommand(...))</c>; am Ende Event-Log mit <see cref="GoldenFile"/> vergleichen.</para>
/// </summary>
public sealed class ReplayFixture
{
    private ReplayFixture(string name, string scriptPath, string replayPath, OrderScript script, IReadOnlyList<MarketEvent> events)
    {
        Name = name;
        ScriptPath = scriptPath;
        ReplayPath = replayPath;
        Script = script;
        Events = events;
    }

    public string Name { get; }
    public string ScriptPath { get; }
    public string ReplayPath { get; }
    public OrderScript Script { get; }
    public IReadOnlyList<MarketEvent> Events { get; }

    public DateTimeOffset StartUtc => ReplayClock.TimeOf(Events[0]);
    public DateTimeOffset EndUtc => ReplayClock.TimeOf(Events[^1]);

    /// <summary>Namen aller Szenarien (Dateien <c>*.orders.json</c>), sortiert.</summary>
    public static IReadOnlyList<string> AllNames(string? directory = null) =>
        System.IO.Directory.GetFiles(directory ?? TestPaths.ReplaysDirectory, "*.orders.json")
            .Select(p => System.IO.Path.GetFileName(p)[..^".orders.json".Length])
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>Für <c>[Theory, MemberData(nameof(ReplayFixture.Names), MemberType = typeof(ReplayFixture))]</c>.</summary>
    public static TheoryData<string> Names()
    {
        var data = new TheoryData<string>();
        foreach (var n in AllNames()) data.Add(n);
        return data;
    }

    public static ReplayFixture Load(string name, string? directory = null)
    {
        var dir = directory ?? TestPaths.ReplaysDirectory;
        var scriptPath = System.IO.Path.Combine(dir, name + ".orders.json");
        var script = OrderScriptJson.Load(scriptPath);
        var replayPath = System.IO.Path.Combine(dir, script.Replay);
        var events = MarketEventJsonl.ReadAll(replayPath);
        if (events.Count == 0) throw new InvalidDataException($"Aufzeichnung {replayPath} ist leer.");
        return new ReplayFixture(name, scriptPath, replayPath, script, events);
    }

    /// <summary>Marktdaten und Orderschritte zeitlich gemischt. Bei gleichem Zeitpunkt kommen Marktdaten zuerst.</summary>
    public IEnumerable<ReplayStep> Timeline()
    {
        int m = 0, o = 0;
        var steps = Script.Steps;
        while (m < Events.Count || o < steps.Count)
        {
            if (o >= steps.Count || (m < Events.Count && ReplayClock.TimeOf(Events[m]) <= steps[o].AtUtc))
            {
                var e = Events[m++];
                yield return new MarketReplayStep(ReplayClock.TimeOf(e), e);
            }
            else
            {
                yield return new OrderReplayStep(steps[o].AtUtc, o, steps[o]);
                o++;
            }
        }
    }

    public override string ToString() => Name;
}
