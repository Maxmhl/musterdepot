using Musterdepot.Core.Serialization;
using Musterdepot.Recorder.Cli;
using Musterdepot.Recorder.Recording;

namespace Musterdepot.Recorder.Inspection;

/// <summary>
/// <c>slice &lt;eingabe&gt; --out &lt;ausgabe&gt; [--from T] [--to T] [--symbols A,B] [--by asof|received] [--force]</c>.
/// Behält Ereignisse mit from &lt;= Zeit &lt; to. T ist ISO 8601 (UTC) oder relativ zum ersten Ereignis (+5m).
/// Status-Ereignisse (Symbol "*") bleiben unabhängig von --symbols erhalten.
/// </summary>
public static class SliceCommand
{
    public static readonly string[] Flags = ["force"];

    public sealed record Options(string Input, string Output, TimeArg? From, TimeArg? To, IReadOnlyList<string> Symbols, bool ByReceived, bool Force);

    public static async Task<int> RunAsync(CliArgs args, TextWriter output, CancellationToken ct)
    {
        args.EnsureOnly("out", "from", "to", "symbols", "by", "force");
        if (args.Positionals.Count != 1) throw new CliException("slice: genau eine Eingabedatei angeben.");
        var by = (args.Get("by") ?? "asof").ToLowerInvariant();
        if (by is not ("asof" or "received")) throw new CliException("--by muss asof oder received sein.");
        var options = new Options(
            args.Positionals[0],
            args.Require("out"),
            args.Get("from") is { } f ? TimeArg.Parse(f) : null,
            args.Get("to") is { } t ? TimeArg.Parse(t) : null,
            args.GetList("symbols"),
            by == "received",
            args.Has("force"));
        var (read, written, errors) = await SliceAsync(options, ct);
        output.WriteLine($"{written:N0} von {read:N0} Ereignissen → {options.Output}{(errors > 0 ? $" ({errors} fehlerhafte Zeilen übersprungen)" : "")}");
        return 0;
    }

    public static async Task<(long Read, long Written, long Errors)> SliceAsync(Options o, CancellationToken ct = default)
    {
        if (!File.Exists(o.Input)) throw new CliException($"Datei nicht gefunden: {o.Input}");
        if (Path.GetFullPath(o.Input) == Path.GetFullPath(o.Output)) throw new CliException("Ein- und Ausgabedatei müssen verschieden sein.");
        if (File.Exists(o.Output))
        {
            if (!o.Force) throw new CliException($"Ausgabedatei existiert bereits: {o.Output} (--force zum Überschreiben).");
            File.Delete(o.Output);
        }
        var symbols = new HashSet<string>(o.Symbols, StringComparer.OrdinalIgnoreCase);
        DateTimeOffset? from = null, to = null;
        bool resolved = false;
        long read = 0, written = 0, errors = 0;
        await using var writer = new MarketEventJsonl.Writer(o.Output);
        await foreach (var line in RecordingReader.ReadAsync(o.Input, ct))
        {
            if (line.Event is not { } e) { errors++; continue; }
            read++;
            var time = o.ByReceived ? ReplayClock.TimeOf(e) : e.AsOfUtc;
            if (!resolved)
            {
                from = o.From?.Resolve(time);
                to = o.To?.Resolve(time);
                resolved = true;
            }
            if (from is { } a && time < a) continue;
            if (to is { } b && time >= b) continue;
            if (symbols.Count > 0 && e.Symbol != "*" && !symbols.Contains(e.Symbol)) continue;
            await writer.WriteAsync(e);
            written++;
        }
        return (read, written, errors);
    }

}
