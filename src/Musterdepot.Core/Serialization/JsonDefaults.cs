using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Musterdepot.Core.Events;
using Musterdepot.Core.MarketData;

namespace Musterdepot.Core.Serialization;

/// <summary>Gemeinsame JSON-Einstellungen (Events, Aufzeichnungen, Protokoll). decimal bleibt verlustfrei.</summary>
public static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        o.Converters.Add(new JsonStringEnumConverter());
        o.MakeReadOnly(populateMissingResolver: true);
        return o;
    }

    public static string SerializeEvent(DomainEvent e) => JsonSerializer.Serialize(e, Options);
    public static DomainEvent DeserializeEvent(string json) => JsonSerializer.Deserialize<DomainEvent>(json, Options)!;
    public static string SerializeMarketEvent(MarketEvent e) => JsonSerializer.Serialize(e, Options);
    public static MarketEvent DeserializeMarketEvent(string json) => JsonSerializer.Deserialize<MarketEvent>(json, Options)!;
}

/// <summary>
/// Aufzeichnungsformat für Marktdaten (Golden Replays, Backtests): eine JSON-Zeile je <see cref="MarketEvent"/>,
/// optional gzip-komprimiert (Dateiendung .jsonl.gz).
/// </summary>
public static class MarketEventJsonl
{
    public static async Task WriteAsync(string path, IEnumerable<MarketEvent> events, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using var file = File.Create(path);
        await using Stream s = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(file, CompressionLevel.Optimal) : file;
        await using var w = new StreamWriter(s, new UTF8Encoding(false));
        foreach (var e in events)
        {
            ct.ThrowIfCancellationRequested();
            await w.WriteLineAsync(JsonDefaults.SerializeMarketEvent(e));
        }
    }

    public static async IAsyncEnumerable<MarketEvent> ReadAsync(string path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var file = File.OpenRead(path);
        await using Stream s = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(file, CompressionMode.Decompress) : file;
        using var r = new StreamReader(s, Encoding.UTF8);
        string? line;
        while ((line = await r.ReadLineAsync(ct)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            yield return JsonDefaults.DeserializeMarketEvent(line);
        }
    }

    public static IReadOnlyList<MarketEvent> ReadAll(string path)
    {
        var list = new List<MarketEvent>();
        using var file = File.OpenRead(path);
        using Stream s = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(file, CompressionMode.Decompress) : file;
        using var r = new StreamReader(s, Encoding.UTF8);
        string? line;
        while ((line = r.ReadLine()) is not null)
            if (!string.IsNullOrWhiteSpace(line)) list.Add(JsonDefaults.DeserializeMarketEvent(line));
        return list;
    }

    /// <summary>Appender für laufende Aufzeichnung (Recorder).</summary>
    public sealed class Writer : IAsyncDisposable
    {
        private readonly StreamWriter _w;
        private readonly SemaphoreSlim _gate = new(1, 1);
        public Writer(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            Stream s = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(file, CompressionLevel.Fastest) : file;
            _w = new StreamWriter(s, new UTF8Encoding(false));
        }
        public async Task WriteAsync(MarketEvent e)
        {
            await _gate.WaitAsync();
            try { await _w.WriteLineAsync(JsonDefaults.SerializeMarketEvent(e)); }
            finally { _gate.Release(); }
        }
        public Task FlushAsync() => _w.FlushAsync();
        public async ValueTask DisposeAsync() => await _w.DisposeAsync();
    }
}
