using Musterdepot.Recorder.Fixtures;
using Musterdepot.Recorder.Inspection;
using Musterdepot.Recorder.Recording;

namespace Musterdepot.Recorder.Cli;

/// <summary>Einstieg: Unterbefehle record, inspect, slice, generate-fixtures. Exitcodes: 0 ok, 1 Argumente, 2 Laufzeitfehler,
/// 3 kein IMarketDataHub registriert, 4 inspect fand fehlerhafte Zeilen.</summary>
public static class RecorderCli
{
    public const string Usage = """
        Musterdepot.Recorder – Marktdaten für Golden Replays und Backtests aufzeichnen und prüfen

        Aufruf:
          Musterdepot.Recorder record --symbols BTC/EUR,ETH/EUR [--minutes 60] [--seconds 0]
                                      [--out pfad.jsonl.gz] [--sources kraken,bitvavo] [--no-rotate] [--verbose]
              Zeichnet alle Ereignisse der Symbole auf (MarketEventJsonl-Format). Ohne --minutes/--seconds bis Strg+C.
              Stündlich neue Datei (pfad-yyyyMMdd-HH.jsonl.gz), außer mit --no-rotate.
              Quellen: kraken, bitvavo, coinbase, gettex, cboe, ecb, fake. Standard-Ziel:
              %LOCALAPPDATA%\Musterdepot\recordings (bzw. MUSTERDEPOT_DATA_DIR\recordings).

          Musterdepot.Recorder inspect <datei>... [--gap-seconds 60] [--max-gaps 20] [--json]
              Statistik: Ereignisse je Typ/Symbol/Quelle, Zeitraum, Reihenfolge, Lücken.

          Musterdepot.Recorder slice <eingabe> --out <ausgabe> [--from T] [--to T] [--symbols A,B]
                                     [--by asof|received] [--force]
              Zeitfenster ausschneiden. T: ISO 8601 (UTC) oder relativ zum ersten Ereignis (+90s, +5m, +1h).

          Musterdepot.Recorder generate-fixtures [--out tests/data/replays] [--seed 20261109]
              Erzeugt die Testszenarien (JSONL + Orderskript) deterministisch neu.

        Ohne Unterbefehl, aber mit --symbols, wird "record" angenommen.
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help" or "-h" or "/?")
            {
                output.WriteLine(Usage);
                return args.Length == 0 ? 1 : 0;
            }
            var (command, rest) = args[0].StartsWith("--", StringComparison.Ordinal)
                ? ("record", args)
                : (args[0].ToLowerInvariant(), args[1..]);
            return command switch
            {
                "record" => await RecordCommand.RunAsync(CliArgs.Parse(rest, RecordCommand.Flags), ct),
                "inspect" => await InspectCommand.RunAsync(CliArgs.Parse(rest, InspectCommand.Flags), output, ct),
                "slice" => await SliceCommand.RunAsync(CliArgs.Parse(rest, SliceCommand.Flags), output, ct),
                "generate-fixtures" => await FixtureGenerator.RunAsync(CliArgs.Parse(rest), output, ct),
                _ => throw new CliException($"Unbekannter Unterbefehl '{args[0]}'."),
            };
        }
        catch (CliException ex)
        {
            error.WriteLine("Fehler: " + ex.Message);
            error.WriteLine("Hilfe: Musterdepot.Recorder --help");
            return 1;
        }
    }
}
