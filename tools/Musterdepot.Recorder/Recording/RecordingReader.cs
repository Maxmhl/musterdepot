using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using Musterdepot.Core.MarketData;
using Musterdepot.Core.Serialization;

namespace Musterdepot.Recorder.Recording;

/// <summary>Eine gelesene Zeile: entweder ein Ereignis oder ein Fehler (z. B. abgeschnittene .gz-Datei nach Absturz).</summary>
public sealed record RecordedLine(long LineNumber, MarketEvent? Event, string? Error, bool Truncated = false);

/// <summary>
/// Fehlertoleranter Leser für Aufzeichnungen im <see cref="MarketEventJsonl"/>-Format. Im Gegensatz zu
/// <see cref="MarketEventJsonl.ReadAsync"/> bricht er bei kaputten Zeilen nicht ab, sondern meldet sie.
/// </summary>
public static class RecordingReader
{
    public static async IAsyncEnumerable<RecordedLine> ReadAsync(string path, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        await using Stream s = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new GZipStream(file, CompressionMode.Decompress)
            : file;
        using var reader = new StreamReader(s, Encoding.UTF8);
        long lineNo = 0;
        var (pending, readError) = await ReadLineSafeAsync(reader, ct);
        while (pending is not null)
        {
            var line = pending;
            lineNo++;
            (pending, readError) = await ReadLineSafeAsync(reader, ct);
            if (string.IsNullOrWhiteSpace(line)) continue;

            MarketEvent? e = null;
            string? parseError = null;
            try
            {
                e = JsonDefaults.DeserializeMarketEvent(line);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException or InvalidOperationException)
            {
                parseError = ex.Message;
            }
            // Eine kaputte letzte Zeile heißt fast immer: Aufzeichnung wurde mitten im Schreiben abgebrochen.
            bool lastLine = pending is null && readError is null;
            yield return parseError is not null && lastLine
                ? new RecordedLine(lineNo, null, "Letzte Zeile unvollständig (Aufzeichnung abgebrochen?): " + parseError, Truncated: true)
                : new RecordedLine(lineNo, e, parseError);
        }
        if (readError is not null)
            yield return new RecordedLine(lineNo + 1, null, readError, Truncated: true);
    }

    private static async Task<(string? Line, string? Error)> ReadLineSafeAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            return (await reader.ReadLineAsync(ct), null);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return (null, $"Datei endet abgeschnitten oder ist beschädigt: {ex.Message}");
        }
    }
}

/// <summary>Zeitachse beim Abspielen: Empfangszeit (bei verzögerten Daten später als AsOfUtc), sonst AsOfUtc.</summary>
public static class ReplayClock
{
    public static DateTimeOffset TimeOf(MarketEvent e) => e.ReceivedUtc == default ? e.AsOfUtc : e.ReceivedUtc;
}

/// <summary>Kurznamen der Ereignistypen (entsprechen dem JSON-Diskriminator <c>$type</c>).</summary>
public static class MarketEventKinds
{
    public static string Of(MarketEvent e) => e switch
    {
        TradeTick => "trade",
        QuoteTick => "quote",
        OrderBookUpdate { IsSnapshot: true } => "book-snapshot",
        OrderBookUpdate => "book-delta",
        CandleUpdate => "candle",
        ConnectionStatusChanged => "status",
        GapDetected => "gap",
        _ => e.GetType().Name,
    };
}
