using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;
using Musterdepot.MarketData.Gettex;

namespace Musterdepot.MarketData.Replay;

public sealed record GettexReplayOptions : ReplayOptions
{
    /// <summary>Einzelne Dateien (CSV oder .csv.gz). Alternativ <see cref="Directory"/> + <see cref="Date"/>.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];
    /// <summary>Ordner mit gettex-Dateien; es werden alle Dateien des Tages <see cref="Date"/> genommen (Name enthält yyyyMMdd).</summary>
    public string? Directory { get; init; }
    public DateOnly? Date { get; init; }
    /// <summary>Optional: Ereignisse erst so viele Minuten nach dem Handelszeitpunkt erscheinen lassen (realistische Verzögerung).</summary>
    public int SimulatedDelayMinutes { get; init; }
    public GettexParserOptions Parser { get; init; } = new();
}

/// <summary>Spielt die gettex-Dateien eines Tages mit simulierter Uhr ab (<c>Quality = Replay</c>).</summary>
public sealed partial class GettexReplaySource : ReplaySourceBase
{
    public const string SourceName = "gettex-replay";
    private readonly GettexReplayOptions _options;

    public GettexReplaySource(GettexReplayOptions options, ManualClock? clock = null, ILogger<GettexReplaySource>? logger = null)
        : base(SourceName, clock ?? new ManualClock(DateTimeOffset.UnixEpoch), options, logger)
    {
        _options = options;
    }

    public override IReadOnlyCollection<AssetClass> AssetClasses { get; } = [AssetClass.Stock, AssetClass.Etf];
    public override int DelayedMinutes => _options.SimulatedDelayMinutes;
    /// <summary>Warnungen beim Einlesen (Formatwechsel, übersprungene Zeilen).</summary>
    public IReadOnlyList<string> LoadWarnings { get; private set; } = [];

    [GeneratedRegex(@"(20\d{2})(\d{2})(\d{2})")]
    private static partial Regex DateInName();

    public IReadOnlyList<string> ResolveFiles()
    {
        var files = new List<string>(_options.Files);
        if (_options.Directory is { } dir && System.IO.Directory.Exists(dir))
        {
            var key = _options.Date?.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            files.AddRange(System.IO.Directory.EnumerateFiles(dir)
                .Where(f => key is null || Path.GetFileName(f).Contains(key, StringComparison.Ordinal))
                .Where(f => f.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".csv.gz", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.Ordinal));
        }
        return files;
    }

    protected override async Task<IReadOnlyList<MarketEvent>> LoadEventsAsync(CancellationToken ct)
    {
        var parser = new GettexCsvParser(_options.Parser);
        var events = new List<MarketEvent>();
        var warnings = new List<string>();
        foreach (var file in ResolveFiles())
        {
            ct.ThrowIfCancellationRequested();
            var date = _options.Date;
            var m = DateInName().Match(Path.GetFileName(file));
            if (date is null && m.Success && DateOnly.TryParseExact(m.Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) date = d;
            GettexParseResult result;
            await using (var fs = File.OpenRead(file))
                result = parser.Parse(fs, date);
            if (result.FormatChanged) warnings.Add($"{Path.GetFileName(file)}: Dateiformat geändert");
            if (!result.FormatRecognized) { warnings.Add($"{Path.GetFileName(file)}: Format nicht erkannt"); continue; }
            foreach (var t in result.Trades)
            {
                events.Add(new TradeTick
                {
                    Symbol = t.Isin, Source = SourceName, AsOfUtc = t.TimeUtc, DelayedMinutes = DelayedMinutes,
                    Quality = DataQuality.Replay, Price = t.Price, Quantity = t.Volume,
                });
                if (t.Bid is not null || t.Ask is not null)
                    events.Add(new QuoteTick
                    {
                        Symbol = t.Isin, Source = SourceName, AsOfUtc = t.TimeUtc, DelayedMinutes = DelayedMinutes,
                        Quality = DataQuality.Replay, Bid = t.Bid, Ask = t.Ask, BidSize = t.BidSize, AskSize = t.AskSize, Last = t.Price,
                    });
            }
        }
        LoadWarnings = warnings;
        foreach (var w in warnings) Logger.LogWarning("gettex-Replay: {Warning}", w);
        return events;
    }

    protected override DateTimeOffset EmitTime(MarketEvent e) => e.AsOfUtc + TimeSpan.FromMinutes(_options.SimulatedDelayMinutes);
}
