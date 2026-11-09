using Microsoft.Extensions.Logging;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;
using Musterdepot.Core.Serialization;

namespace Musterdepot.MarketData.Replay;

public sealed record RecordedReplayOptions : ReplayOptions
{
    /// <summary>Name der Quelle (für Routing im Hub).</summary>
    public string Name { get; init; } = "replay";
    /// <summary>Ursprüngliche Quelle ("kraken" …) in den Ereignissen beibehalten; sonst wird <see cref="Name"/> eingetragen.</summary>
    public bool PreserveSource { get; init; } = true;
    /// <summary>Qualitätsflag überschreiben (z. B. Replay); null = wie aufgezeichnet (Golden Replays).</summary>
    public DataQuality? OverrideQuality { get; init; }
    /// <summary>Aufgezeichnete Statusmeldungen mit abspielen (Standard: nein, sie würden das Failover im Hub stören).</summary>
    public bool IncludeStatusEvents { get; init; }
}

/// <summary>
/// Spielt eine <see cref="MarketEventJsonl"/>-Aufzeichnung (.jsonl oder .jsonl.gz) mit simulierter Uhr ab –
/// für Golden Replays und Backtests.
/// </summary>
public sealed class RecordedMarketDataSource : ReplaySourceBase
{
    private readonly IReadOnlyList<string> _paths;
    private readonly RecordedReplayOptions _options;

    public RecordedMarketDataSource(string path, ManualClock? clock = null, RecordedReplayOptions? options = null, ILogger<RecordedMarketDataSource>? logger = null)
        : this([path], clock, options, logger) { }

    public RecordedMarketDataSource(IReadOnlyList<string> paths, ManualClock? clock = null, RecordedReplayOptions? options = null, ILogger<RecordedMarketDataSource>? logger = null)
        : base((options ?? new()).Name, clock ?? new ManualClock(DateTimeOffset.UnixEpoch), options ?? new(), logger)
    {
        _paths = paths;
        _options = options ?? new();
    }

    protected override DataQuality DefaultQuality => _options.OverrideQuality ?? DataQuality.Replay;

    protected override async Task<IReadOnlyList<MarketEvent>> LoadEventsAsync(CancellationToken ct)
    {
        var list = new List<MarketEvent>();
        foreach (var p in _paths)
            await foreach (var e in MarketEventJsonl.ReadAsync(p, ct).ConfigureAwait(false))
            {
                if (e is ConnectionStatusChanged && !_options.IncludeStatusEvents) continue;
                list.Add(e);
            }
        return list;
    }

    protected override MarketEvent Restamp(MarketEvent e, DateTimeOffset nowUtc)
        => e with
        {
            ReceivedUtc = nowUtc,
            Quality = _options.OverrideQuality ?? e.Quality,
            Source = _options.PreserveSource ? e.Source : Name,
        };
}
