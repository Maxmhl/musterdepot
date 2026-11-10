using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Musterdepot.Commands;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Commands;
using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;
using Musterdepot.Engine;
using Musterdepot.MarketData;
using Musterdepot.MarketData.Replay;
using Musterdepot.Mcp;
using Musterdepot.Persistence;

namespace Musterdepot.IntegrationTests.Infrastructure;

public sealed class TestHostOptions
{
    /// <summary>Startzeit der <see cref="ManualClock"/>.</summary>
    public DateTimeOffset StartUtc { get; set; } = new(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
    public int Seed { get; set; } = 42;
    /// <summary>Standard: nur Fake-Daten, keine Netzwerkquellen.</summary>
    public DataSourceSettings DataSources { get; set; } = new()
    {
        KrakenEnabled = false, BitvavoEnabled = false, CoinbaseEnabled = false, GettexEnabled = false,
        CboeEnabled = false, EcbEnabled = false, UseFakeData = true,
    };
    /// <summary>
    /// Statt Fake-Daten eine Aufzeichnung abspielen (<see cref="RecordedMarketDataSource"/> auf der Uhr des Hosts,
    /// ohne AutoPlay – Fortschritt über <see cref="TestHost.Replay"/>.AdvanceTo). Schaltet alle anderen Quellen ab.
    /// </summary>
    public string? ReplayFile { get; set; }
    /// <summary>Vorhandenes Datenverzeichnis wiederverwenden (Neustart-Tests); wird beim Dispose nicht gelöscht.</summary>
    public string? DataDirectory { get; set; }
    public bool AddPersistence { get; set; } = true;
    public bool AddMarketData { get; set; } = true;
    public bool AddEngine { get; set; } = true;
    public bool AddCommands { get; set; } = true;
    public bool AddMcp { get; set; } = true;
    /// <summary>MCP-Server: Port 0 (zufällig), ohne Named Pipe, Token-Dateien im Datenverzeichnis.</summary>
    public Action<McpOptions>? ConfigureMcp { get; set; }
    public CommandOptions Commands { get; set; } = new()
    {
        ApprovalTimeout = TimeSpan.FromSeconds(10),
        ClockTickInterval = TimeSpan.FromMilliseconds(20),
        QuoteThrottleInterval = TimeSpan.FromMilliseconds(20),
    };
    public LogLevel MinimumLogLevel { get; set; } = LogLevel.Warning;
    /// <summary>Wird nach allen AddMusterdepot*-Aufrufen ausgeführt (z. B. Quelle durch Replay ersetzen).</summary>
    public Action<IServiceCollection>? ConfigureServices { get; set; }
}

/// <summary>
/// DI-Container wie in der App, aber mit temporärem Datenverzeichnis (<c>MUSTERDEPOT_DATA_DIR</c>), fester Uhr,
/// fortlaufenden IDs, festem Zufall und Fake- oder Replay-Marktdaten.
/// <para>Die Umgebungsvariable gilt prozessweit; deshalb existiert höchstens ein TestHost gleichzeitig
/// (weitere warten in <see cref="Create"/>). Immer mit <c>await using</c> verwenden.</para>
/// </summary>
public sealed class TestHost : IAsyncDisposable
{
    public const string DataDirVariable = "MUSTERDEPOT_DATA_DIR";
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly string? _previousDataDir;
    private readonly bool _ownsDataDirectory;
    private readonly ServiceProvider _provider;
    private bool _disposed;

    private TestHost(ServiceProvider provider, string dataDirectory, bool ownsDataDirectory, ManualClock clock, string? previousDataDir)
    {
        _provider = provider;
        DataDirectory = dataDirectory;
        _ownsDataDirectory = ownsDataDirectory;
        Clock = clock;
        _previousDataDir = previousDataDir;
    }

    public IServiceProvider Services => _provider;
    public string DataDirectory { get; }
    public ManualClock Clock { get; }

    public T Get<T>() where T : notnull => _provider.GetRequiredService<T>();
    public T? TryGet<T>() => _provider.GetService<T>();

    public ICommandBus Bus => Get<ICommandBus>();
    public ITradingEngine Engine => Get<ITradingEngine>();
    public IMusterdepotRuntime Runtime => Get<IMusterdepotRuntime>();
    /// <summary>Replay-Quelle (nur mit <see cref="TestHostOptions.ReplayFile"/>).</summary>
    public RecordedMarketDataSource? Replay => TryGet<RecordedMarketDataSource>();

    public static TestHost Create(Action<TestHostOptions>? configure = null)
    {
        var options = new TestHostOptions();
        configure?.Invoke(options);

        if (!Gate.Wait(TimeSpan.FromMinutes(2)))
            throw new TimeoutException("Ein anderer TestHost wurde nicht freigegeben (fehlendes await using?).");
        var previous = Environment.GetEnvironmentVariable(DataDirVariable);
        var owns = options.DataDirectory is null;
        string? dataDir = null;
        try
        {
            dataDir = options.DataDirectory ?? TestPaths.NewTempDirectory("musterdepot-host");
            Directory.CreateDirectory(dataDir);
            Environment.SetEnvironmentVariable(DataDirVariable, dataDir);

            var clock = new ManualClock(options.StartUtc);
            var services = new ServiceCollection();
            services.AddLogging(b => b.SetMinimumLevel(options.MinimumLogLevel));
            services.AddSingleton(options.Commands);
            if (options.ReplayFile is { } replayFile)
            {
                options.DataSources = new DataSourceSettings
                {
                    KrakenEnabled = false, BitvavoEnabled = false, CoinbaseEnabled = false, GettexEnabled = false,
                    CboeEnabled = false, EcbEnabled = false, UseFakeData = false,
                };
                var replay = new RecordedMarketDataSource(replayFile, clock, new RecordedReplayOptions { AutoPlay = false, StartAtFirstEvent = false });
                services.AddSingleton(replay);
                services.AddSingleton<IMarketDataSource>(replay);
            }
            if (options.AddPersistence) services.AddMusterdepotPersistence(AppPaths.DatabasePath);
            if (options.AddMarketData) services.AddMusterdepotMarketData(options.DataSources);
            if (options.AddEngine) services.AddMusterdepotEngine();
            if (options.AddCommands) services.AddMusterdepotCommands();
            var tokens = Path.Combine(dataDir, "tokens");
            if (options.AddMcp) services.AddMusterdepotMcp(o =>
            {
                o.Port = 0;
                o.EnablePipe = false;
                o.PipeName = "mdp-it-" + Guid.NewGuid().ToString("N")[..12];
                o.TokensDirectory = tokens;
                options.ConfigureMcp?.Invoke(o);
            });

            // Deterministische Infrastruktur gewinnt immer, egal was die Erweiterungen registriert haben.
            services.RemoveAll<IClock>();
            services.RemoveAll<IIdGenerator>();
            services.RemoveAll<IRandomSource>();
            services.AddSingleton<IClock>(clock);
            services.AddSingleton(clock);
            services.AddSingleton<IIdGenerator>(new SequentialIdGenerator());
            services.AddSingleton<IRandomSource>(new SeededRandom(options.Seed));
            options.ConfigureServices?.Invoke(services);

            var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            return new TestHost(provider, dataDir, owns, clock, previous);
        }
        catch
        {
            Environment.SetEnvironmentVariable(DataDirVariable, previous);
            if (owns && dataDir is not null) TestPaths.TryDeleteDirectory(dataDir);
            Gate.Release();
            throw;
        }
    }

    /// <summary>Speichert Einstellungen über den <see cref="ISettingsStore"/> (läuft durch den SettingsService).</summary>
    public async Task<AppSettings> UpdateSettingsAsync(Func<AppSettings, AppSettings> change)
    {
        var store = Get<ISettingsStore>();
        var updated = change(await store.LoadAsync());
        await store.SaveAsync(updated);
        return updated;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (TryGet<IMcpServerHost>() is { IsRunning: true } mcp) await mcp.StopAsync();
            await _provider.DisposeAsync();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Environment.SetEnvironmentVariable(DataDirVariable, _previousDataDir);
            if (_ownsDataDirectory) TestPaths.TryDeleteDirectory(DataDirectory);
            Gate.Release();
        }
    }

    /// <summary>Wartet (echte Zeit), bis die Bedingung erfüllt ist.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10_000, string? what = null)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException($"Bedingung nicht rechtzeitig erfüllt{(what is null ? "" : ": " + what)}.");
            await Task.Delay(10);
        }
    }

    public static async Task WaitUntilAsync(Func<Task<bool>> condition, int timeoutMs = 10_000, string? what = null)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!await condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException($"Bedingung nicht rechtzeitig erfüllt{(what is null ? "" : ": " + what)}.");
            await Task.Delay(10);
        }
    }
}

/// <summary>
/// Alle Testklassen, die einen <see cref="TestHost"/> verwenden, laufen nacheinander. Sonst könnten parallele Tests
/// synchron in <see cref="TestHost.Create"/> warten und dabei alle Threads des xUnit-Synchronisationskontexts belegen,
/// die der gerade laufende Host für seine await-Fortsetzungen braucht (Verklemmung bis zum Timeout).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TestHostCollection
{
    public const string Name = "TestHost";
}
