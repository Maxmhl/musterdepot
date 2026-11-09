using System.Globalization;
using System.Text;
using System.Text.Json;
using Musterdepot.Core.Serialization;
using Musterdepot.Recorder.Cli;
using Musterdepot.Recorder.Recording;

namespace Musterdepot.Recorder.Inspection;

/// <summary><c>inspect &lt;datei&gt;... [--gap-seconds 60] [--max-gaps 20] [--json]</c></summary>
public static class InspectCommand
{
    public static readonly string[] Flags = ["json"];

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonDefaults.Options) { WriteIndented = true, NewLine = "\n" };

    public static async Task<int> RunAsync(CliArgs args, TextWriter output, CancellationToken ct)
    {
        args.EnsureOnly("gap-seconds", "max-gaps", "json");
        if (args.Positionals.Count == 0) throw new CliException("inspect: mindestens eine Datei angeben.");
        var gapSeconds = args.GetDouble("gap-seconds", 60);
        var maxGaps = args.GetInt("max-gaps", 20);
        var stats = await AnalyzeAsync(args.Positionals, TimeSpan.FromSeconds(gapSeconds), ct);
        if (args.Has("json")) output.WriteLine(JsonSerializer.Serialize(stats, JsonOptions));
        else output.Write(FormatText(stats, maxGaps));
        return stats.ParseErrors > 0 ? 4 : 0;
    }

    public static async Task<RecordingStats> AnalyzeAsync(IEnumerable<string> files, TimeSpan silenceThreshold, CancellationToken ct = default)
    {
        var stats = new RecordingStats(silenceThreshold);
        foreach (var file in files)
        {
            if (!File.Exists(file)) throw new CliException($"Datei nicht gefunden: {file}");
            stats.Files.Add(Path.GetFileName(file));
            await foreach (var line in RecordingReader.ReadAsync(file, ct))
            {
                if (line.Event is { } e) stats.Add(e);
                else stats.AddError(file, line);
            }
        }
        return stats;
    }

    public static string FormatText(RecordingStats s, int maxGaps)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine(inv, $"Dateien:        {string.Join(", ", s.Files)}");
        sb.AppendLine(inv, $"Ereignisse:     {s.Events:N0}   Fehler: {s.ParseErrors}{(s.Truncated ? "   (Datei endet abgeschnitten!)" : "")}");
        sb.AppendLine(inv, $"Zeitraum AsOf:  {s.FirstAsOfUtc:O} – {s.LastAsOfUtc:O}   ({TimeSpan.FromSeconds(s.DurationSeconds):c})");
        if (s.FirstReceivedUtc is not null)
            sb.AppendLine(inv, $"Empfangen:      {s.FirstReceivedUtc:O} – {s.LastReceivedUtc:O}");
        sb.AppendLine(inv, $"Reihenfolge:    {s.AsOfOutOfOrder} AsOf-Rücksprünge je Symbol, {s.ReceivedOutOfOrder} Empfangs-Rücksprünge");
        sb.AppendLine("Je Typ:         " + string.Join(", ", s.ByType.Select(kv => $"{kv.Key}={kv.Value}")));
        sb.AppendLine("Quellen:        " + string.Join(", ", s.BySource.Select(kv => $"{kv.Key}={kv.Value}")));
        sb.AppendLine("Qualität:       " + string.Join(", ", s.ByQuality.Select(kv => $"{kv.Key}={kv.Value}")));
        sb.AppendLine("Verzögerung:    " + string.Join(", ", s.ByDelayedMinutes.Select(kv => $"{kv.Key} min={kv.Value}")));
        if (s.BackfilledCandles > 0) sb.AppendLine(inv, $"Nachgeladen:    {s.BackfilledCandles} Kerzen (GapDetected)");
        sb.AppendLine();
        sb.AppendLine("Je Symbol:");
        foreach (var (symbol, kinds) in s.BySymbol)
            sb.AppendLine(inv, $"  {symbol,-16} {kinds.Values.Sum(),8:N0}   {string.Join(", ", kinds.Select(kv => $"{kv.Key}={kv.Value}"))}");
        sb.AppendLine();
        sb.AppendLine(inv, $"Lücken (Stille > {s.SilenceThresholdSeconds:0.#} s, GapDetected, fehlende Kerzen): {s.Gaps.Count}");
        foreach (var g in s.Gaps.Take(maxGaps))
            sb.AppendLine(inv, $"  {g.Symbol,-16} {g.Kind,-16} {g.FromUtc:O} – {g.ToUtc:O} ({g.Seconds:0.#} s){(g.Detail is null ? "" : "  " + g.Detail)}");
        if (s.Gaps.Count > maxGaps) sb.AppendLine(inv, $"  … {s.Gaps.Count - maxGaps} weitere");
        if (s.Errors.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Fehler:");
            foreach (var e in s.Errors) sb.AppendLine("  " + e);
        }
        return sb.ToString();
    }
}
