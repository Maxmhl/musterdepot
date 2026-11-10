using System.Runtime.CompilerServices;
using System.Text.Json;
using Musterdepot.Core.Serialization;
using Musterdepot.Engine.Backtest;

namespace Musterdepot.Engine.Tests;

/// <summary>
/// Golden Replays: aufgezeichnete Marktdaten (Golden/&lt;Szenario&gt;/market.jsonl) + Orderskript (script.json)
/// → exakt erwartetes Event-Log (expected-events.json) und Zusammenfassung (expected-summary.json).
/// Aktualisieren: <c>UPDATE_GOLDEN=1 dotnet test --filter GoldenReplayTests</c>.
/// </summary>
public class GoldenReplayTests
{
    private static readonly JsonSerializerOptions Indented = new(JsonDefaults.Options) { WriteIndented = true };

    private static string GoldenDir([CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "Golden");

    public static TheoryData<string> Scenarios => new() { "crypto-btc", "stock-delayed", "gap-standby" };

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Replay_matches_golden_event_log(string scenario)
    {
        var dir = Path.Combine(GoldenDir(), scenario);
        var script = BacktestScript.Load(Path.Combine(dir, "script.json"));
        var result = new BacktestRunner().Run(Path.Combine(dir, "market.jsonl"), script);

        var events = result.EventsToJson().ReplaceLineEndings("\n") + "\n";
        var summary = JsonSerializer.Serialize(new
        {
            result.Stats,
            result.Portfolio,
            Steps = result.StepResults.Select(s => new { s.Step.AtUtc, Accepted = s.Result?.Accepted, s.Result?.RejectReason, s.Result?.RiskRule, s.Result?.OrderId }),
        }, Indented).ReplaceLineEndings("\n") + "\n";

        var eventsPath = Path.Combine(dir, "expected-events.json");
        var summaryPath = Path.Combine(dir, "expected-summary.json");
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            File.WriteAllText(eventsPath, events);
            File.WriteAllText(summaryPath, summary);
        }
        Assert.True(File.Exists(eventsPath), $"Golden-Datei fehlt: {eventsPath} (UPDATE_GOLDEN=1 setzen)");
        Assert.Equal(File.ReadAllText(eventsPath).ReplaceLineEndings("\n"), events);
        Assert.Equal(File.ReadAllText(summaryPath).ReplaceLineEndings("\n"), summary);

        // Event-Log ist abspielbar und ergibt denselben Zustand
        var restored = new TradingEngine(new Musterdepot.Core.Abstractions.ManualClock(DateTimeOffset.UnixEpoch), new Musterdepot.Core.Abstractions.SequentialIdGenerator());
        restored.LoadFromEvents(File.ReadAllLines(eventsPath).Length > 0
            ? JsonSerializer.Deserialize<List<Musterdepot.Core.Events.DomainEvent>>(File.ReadAllText(eventsPath), JsonDefaults.Options)!
            : []);
        Assert.Equal(ReplayTests.Fingerprint(result.Engine.GetAccountState(script.AccountId)!), ReplayTests.Fingerprint(restored.GetAccountState(script.AccountId)!));
    }
}
