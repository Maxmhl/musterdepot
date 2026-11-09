using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;
using Musterdepot.MarketData;
using Musterdepot.Recorder.Cli;

namespace Musterdepot.Recorder.Recording;

/// <summary>
/// <c>record --symbols BTC/EUR,ETH/EUR [--minutes 60] [--out pfad.jsonl.gz] [--sources kraken,bitvavo] [--no-rotate] [--verbose]</c>
/// </summary>
public static class RecordCommand
{
    public static readonly string[] Flags = ["no-rotate", "verbose"];
    public static readonly string[] KnownSources = ["kraken", "bitvavo", "coinbase", "gettex", "cboe", "ecb", "fake"];

    public static async Task<int> RunAsync(CliArgs args, CancellationToken ct)
    {
        args.EnsureOnly("symbols", "minutes", "out", "sources", "no-rotate", "verbose");
        var symbols = args.GetList("symbols");
        if (symbols.Count == 0) throw new CliException("Option --symbols fehlt (z. B. --symbols BTC/EUR,ETH/EUR).");
        var minutes = args.GetInt("minutes", 0);
        if (minutes < 0) throw new CliException("--minutes muss >= 0 sein (0 = bis Strg+C).");
        var sources = args.Has("sources") ? args.GetList("sources") : ["kraken", "bitvavo"];
        var settings = BuildSettings(sources);
        var rotate = !args.Has("no-rotate");
        var outPath = ResolveOutPath(args.Get("out"));
        var clock = SystemClock.Instance;

        var services = new ServiceCollection();
        services.AddLogging(b => b
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
            .SetMinimumLevel(args.Has("verbose") ? LogLevel.Debug : LogLevel.Warning));
        services.TryAddSingleton<IClock>(clock);
        services.TryAddSingleton<IIdGenerator, GuidIdGenerator>();
        services.TryAddSingleton<IRandomSource>(new SeededRandom(Environment.TickCount));
        services.AddMusterdepotMarketData(settings);

        await using var provider = services.BuildServiceProvider();
        var hub = provider.GetService<IMarketDataHub>();
        if (hub is null)
        {
            Console.Error.WriteLine(
                "Fehler: Kein IMarketDataHub registriert. AddMusterdepotMarketData(...) aus Musterdepot.MarketData " +
                "liefert noch keine Implementierung (Workstream 1). Ohne Hub kann nichts aufgezeichnet werden.");
            return 3;
        }

        var symbolSet = new HashSet<string>(symbols, StringComparer.OrdinalIgnoreCase);
        var channel = Channel.CreateUnbounded<MarketEvent>(new UnboundedChannelOptions { SingleReader = true });
        EventHandler<MarketEvent> handler = (_, e) =>
        {
            if (e.Symbol != "*" && !symbolSet.Contains(e.Symbol)) return;
            channel.Writer.TryWrite(e.ReceivedUtc == default ? e with { ReceivedUtc = clock.UtcNow } : e);
        };

        using var stopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (minutes > 0) stopCts.CancelAfter(TimeSpan.FromMinutes(minutes));

        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        await using var writer = new RotatingRecordingWriter(outPath, rotate, clock);
        var consumer = Task.Run(async () =>
        {
            var lastFlush = clock.UtcNow;
            var lastProgress = clock.UtcNow;
            await foreach (var e in channel.Reader.ReadAllAsync(CancellationToken.None))
            {
                await writer.WriteAsync(e);
                var key = $"{e.Symbol} {MarketEventKinds.Of(e)}";
                counts[key] = counts.GetValueOrDefault(key) + 1;
                var now = clock.UtcNow;
                if (now - lastFlush > TimeSpan.FromSeconds(5))
                {
                    await writer.FlushAsync();
                    lastFlush = now;
                }
                if (now - lastProgress > TimeSpan.FromSeconds(30))
                {
                    Console.WriteLine($"{now:HH:mm:ss} {writer.EventsWritten:N0} Ereignisse → {writer.CurrentPath}");
                    lastProgress = now;
                }
            }
        });

        Console.WriteLine($"Aufzeichnung: {string.Join(", ", symbols)} von {string.Join(", ", sources)}" +
                          (minutes > 0 ? $" für {minutes} Min." : " bis Strg+C") +
                          $" → {(rotate ? RotatingRecordingWriter.PathForHour(outPath, clock.UtcNow) + " (stündlich rotiert)" : outPath)}");

        hub.EventReceived += handler;
        int exitCode = 0;
        try
        {
            await hub.StartAsync(stopCts.Token);
            await hub.SubscribeAsync(symbols, stopCts.Token);
            await Task.Delay(Timeout.Infinite, stopCts.Token);
        }
        catch (OperationCanceledException) when (stopCts.IsCancellationRequested)
        {
            // Ende durch Strg+C oder --minutes.
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fehler während der Aufzeichnung: {ex.Message}");
            exitCode = 2;
        }
        finally
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await hub.UnsubscribeAsync(symbols, shutdown.Token); } catch (Exception ex) { Console.Error.WriteLine($"Abmelden fehlgeschlagen: {ex.Message}"); }
            try { await hub.StopAsync(shutdown.Token); } catch (Exception ex) { Console.Error.WriteLine($"Stoppen fehlgeschlagen: {ex.Message}"); }
            hub.EventReceived -= handler;
            channel.Writer.TryComplete();
            await consumer;
            await writer.DisposeAsync();
        }

        Console.WriteLine($"Fertig: {writer.EventsWritten:N0} Ereignisse in {writer.Files.Count} Datei(en).");
        foreach (var f in writer.Files) Console.WriteLine("  " + f);
        foreach (var (key, n) in counts) Console.WriteLine($"  {key,-30} {n,10:N0}");
        return exitCode;
    }

    public static DataSourceSettings BuildSettings(IReadOnlyList<string> sources)
    {
        var set = new HashSet<string>(sources.Select(s => s.ToLowerInvariant()));
        var unknown = set.Except(KnownSources).ToList();
        if (unknown.Count > 0)
            throw new CliException($"Unbekannte Quelle(n): {string.Join(", ", unknown)}. Bekannt: {string.Join(", ", KnownSources)}");
        return new DataSourceSettings
        {
            KrakenEnabled = set.Contains("kraken"),
            BitvavoEnabled = set.Contains("bitvavo"),
            CoinbaseEnabled = set.Contains("coinbase"),
            GettexEnabled = set.Contains("gettex"),
            CboeEnabled = set.Contains("cboe"),
            EcbEnabled = set.Contains("ecb"),
            UseFakeData = set.Contains("fake"),
        };
    }

    /// <summary>Ohne --out: <c>%LOCALAPPDATA%\Musterdepot\recordings\recording.jsonl.gz</c>; ein Ordner bekommt diesen Dateinamen.</summary>
    public static string ResolveOutPath(string? outArg)
    {
        const string defaultName = "recording.jsonl.gz";
        if (string.IsNullOrWhiteSpace(outArg)) return Path.Combine(AppPaths.RecordingsDirectory, defaultName);
        if (Directory.Exists(outArg) || outArg.EndsWith('/') || outArg.EndsWith('\\'))
            return Path.Combine(outArg, defaultName);
        return Path.GetFullPath(outArg);
    }
}
