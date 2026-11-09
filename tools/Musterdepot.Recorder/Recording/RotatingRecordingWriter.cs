using System.Globalization;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.MarketData;
using Musterdepot.Core.Serialization;

namespace Musterdepot.Recorder.Recording;

/// <summary>
/// Schreibt Marktereignisse über <see cref="MarketEventJsonl.Writer"/> und beginnt jede volle Stunde (UTC, laut Uhr)
/// eine neue Datei: <c>btc.jsonl.gz</c> → <c>btc-20261109-10.jsonl.gz</c>, <c>btc-20261109-11.jsonl.gz</c> …
/// Existiert eine Stundendatei schon (Neustart), wird <c>-2</c>, <c>-3</c> … angehängt statt an ein gzip anzuhängen.
/// </summary>
public sealed class RotatingRecordingWriter(string basePath, bool rotateHourly, IClock clock) : IAsyncDisposable
{
    private readonly List<string> _files = [];
    private MarketEventJsonl.Writer? _current;
    private DateTimeOffset _currentHour;

    public IReadOnlyList<string> Files => _files;
    public string? CurrentPath => _files.Count > 0 && _current is not null ? _files[^1] : null;
    public long EventsWritten { get; private set; }

    public async Task WriteAsync(MarketEvent e)
    {
        var hour = HourOf(clock.UtcNow);
        if (_current is null || (rotateHourly && hour != _currentHour))
        {
            await CloseCurrentAsync();
            var path = rotateHourly ? UniquePath(PathForHour(basePath, hour)) : basePath;
            _current = new MarketEventJsonl.Writer(path);
            _currentHour = hour;
            _files.Add(path);
        }
        await _current.WriteAsync(e);
        EventsWritten++;
    }

    public Task FlushAsync() => _current?.FlushAsync() ?? Task.CompletedTask;

    public async ValueTask DisposeAsync() => await CloseCurrentAsync();

    private async Task CloseCurrentAsync()
    {
        if (_current is null) return;
        await _current.DisposeAsync();
        _current = null;
    }

    private static DateTimeOffset HourOf(DateTimeOffset t)
    {
        t = t.ToUniversalTime();
        return new DateTimeOffset(t.Year, t.Month, t.Day, t.Hour, 0, 0, TimeSpan.Zero);
    }

    /// <summary>Hängt <c>-yyyyMMdd-HH</c> vor die Endung (.jsonl.gz, .jsonl oder sonstige) an.</summary>
    public static string PathForHour(string basePath, DateTimeOffset hourUtc)
    {
        var (stem, ext) = SplitExtension(basePath);
        return stem + "-" + hourUtc.ToUniversalTime().ToString("yyyyMMdd-HH", CultureInfo.InvariantCulture) + ext;
    }

    public static (string Stem, string Extension) SplitExtension(string path)
    {
        foreach (var ext in new[] { ".jsonl.gz", ".jsonl" })
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return (path[..^ext.Length], path[^ext.Length..]);
        var e = Path.GetExtension(path);
        return (path[..^e.Length], e);
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var (stem, ext) = SplitExtension(path);
        for (int i = 2; ; i++)
        {
            var candidate = $"{stem}-{i}{ext}";
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
