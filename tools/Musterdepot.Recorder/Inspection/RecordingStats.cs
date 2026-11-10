using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;
using Musterdepot.Recorder.Recording;

namespace Musterdepot.Recorder.Inspection;

/// <summary>Eine gefundene Lücke. Kind: <c>silence</c> (keine Ereignisse eines Symbols länger als Schwelle),
/// <c>gap-detected</c> (von der Quelle gemeldetes <see cref="GapDetected"/>), <c>missing-candles</c> (fehlende Kerzen).</summary>
public sealed record RecordingGap(string Symbol, string Kind, DateTimeOffset FromUtc, DateTimeOffset ToUtc, double Seconds, string? Detail = null);

/// <summary>Statistik über eine oder mehrere Aufzeichnungen (Ausgabe von <c>inspect</c>, auch als Golden-Datei genutzt).</summary>
public sealed class RecordingStats
{
    private readonly TimeSpan _silenceThreshold;
    private readonly Dictionary<string, DateTimeOffset> _lastAsOfBySymbol = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Symbol, Timeframe Tf), DateTimeOffset> _lastCandle = [];
    private DateTimeOffset? _lastReceived;

    public RecordingStats(TimeSpan silenceThreshold) => _silenceThreshold = silenceThreshold;

    public double SilenceThresholdSeconds => _silenceThreshold.TotalSeconds;
    public List<string> Files { get; } = [];
    public long Events { get; private set; }
    public long ParseErrors { get; private set; }
    public bool Truncated { get; private set; }
    public DateTimeOffset? FirstAsOfUtc { get; private set; }
    public DateTimeOffset? LastAsOfUtc { get; private set; }
    public DateTimeOffset? FirstReceivedUtc { get; private set; }
    public DateTimeOffset? LastReceivedUtc { get; private set; }
    public double DurationSeconds => FirstAsOfUtc is { } a && LastAsOfUtc is { } b ? (b - a).TotalSeconds : 0;
    public SortedDictionary<string, long> ByType { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, SortedDictionary<string, long>> BySymbol { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, long> BySource { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, long> ByQuality { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, long> ByDelayedMinutes { get; } = new(StringComparer.Ordinal);
    /// <summary>AsOfUtc springt je Symbol zurück (ohne Status-Ereignisse "*" und ohne abgeschlossene Kerzen).</summary>
    public long AsOfOutOfOrder { get; private set; }
    /// <summary>ReceivedUtc springt über die ganze Datei zurück.</summary>
    public long ReceivedOutOfOrder { get; private set; }
    public long BackfilledCandles { get; private set; }
    public List<RecordingGap> Gaps { get; } = [];
    public List<string> Errors { get; } = [];

    public void AddError(string file, RecordedLine line)
    {
        ParseErrors++;
        if (line.Truncated) Truncated = true;
        if (Errors.Count < 20) Errors.Add($"{Path.GetFileName(file)}:{line.LineNumber}: {line.Error}");
    }

    public void Add(MarketEvent e)
    {
        Events++;
        var kind = MarketEventKinds.Of(e);
        Inc(ByType, kind);
        if (!BySymbol.TryGetValue(e.Symbol, out var perSymbol))
            BySymbol[e.Symbol] = perSymbol = new SortedDictionary<string, long>(StringComparer.Ordinal);
        Inc(perSymbol, kind);
        Inc(BySource, e.Source);
        Inc(ByQuality, e.Quality.ToString());
        Inc(ByDelayedMinutes, e.DelayedMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (FirstAsOfUtc is null || e.AsOfUtc < FirstAsOfUtc) FirstAsOfUtc = e.AsOfUtc;
        if (LastAsOfUtc is null || e.AsOfUtc > LastAsOfUtc) LastAsOfUtc = e.AsOfUtc;
        if (e.ReceivedUtc != default)
        {
            if (FirstReceivedUtc is null || e.ReceivedUtc < FirstReceivedUtc) FirstReceivedUtc = e.ReceivedUtc;
            if (LastReceivedUtc is null || e.ReceivedUtc > LastReceivedUtc) LastReceivedUtc = e.ReceivedUtc;
            if (_lastReceived is { } lr && e.ReceivedUtc < lr) ReceivedOutOfOrder++;
            _lastReceived = e.ReceivedUtc;
        }

        if (e.Symbol != "*")
        {
            if (_lastAsOfBySymbol.TryGetValue(e.Symbol, out var prev))
            {
                // Eine abgeschlossene Kerze trägt das Periodenende als AsOf und folgt dem Trade, der die Periode
                // beendet hat (so meldet es der Hub) – das ist kein Rücksprung.
                if (e.AsOfUtc < prev) { if (e is not CandleUpdate { Candle.IsClosed: true }) AsOfOutOfOrder++; }
                else if (e.AsOfUtc - prev > _silenceThreshold && e is not GapDetected)
                    Gaps.Add(new RecordingGap(e.Symbol, "silence", prev, e.AsOfUtc, (e.AsOfUtc - prev).TotalSeconds));
            }
            if (!_lastAsOfBySymbol.TryGetValue(e.Symbol, out var p2) || e.AsOfUtc > p2)
                _lastAsOfBySymbol[e.Symbol] = e.AsOfUtc;
        }

        switch (e)
        {
            case CandleUpdate cu:
                TrackCandle(cu.Candle);
                break;
            case GapDetected gap:
                Gaps.Add(new RecordingGap(e.Symbol, "gap-detected", gap.GapStartUtc, gap.GapEndUtc,
                    (gap.GapEndUtc - gap.GapStartUtc).TotalSeconds, $"{gap.BackfilledCandles.Count} Kerzen nachgeladen"));
                foreach (var c in gap.BackfilledCandles)
                {
                    BackfilledCandles++;
                    TrackCandle(c);
                }
                break;
        }
    }

    private void TrackCandle(Candle c)
    {
        if (!c.IsClosed) return;
        var key = (c.Symbol, c.Timeframe);
        if (_lastCandle.TryGetValue(key, out var last))
        {
            if (c.OpenTimeUtc <= last) return;
            var step = c.Timeframe.ToTimeSpan();
            if (c.Timeframe != Timeframe.W1 && c.OpenTimeUtc - last > step)
            {
                var missing = (int)((c.OpenTimeUtc - last).Ticks / step.Ticks) - 1;
                Gaps.Add(new RecordingGap(c.Symbol, "missing-candles", last + step, c.OpenTimeUtc,
                    (c.OpenTimeUtc - last - step).TotalSeconds, $"{missing} {c.Timeframe}-Kerze(n) fehlen"));
            }
        }
        _lastCandle[key] = c.OpenTimeUtc;
    }

    private static void Inc(SortedDictionary<string, long> d, string key) => d[key] = d.GetValueOrDefault(key) + 1;
}
