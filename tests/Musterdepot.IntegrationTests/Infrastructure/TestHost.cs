using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Musterdepot.Commands;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;
using Musterdepot.Engine;
using Musterdepot.MarketData;
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
    public bool AddPersistence { get; set; } = true;
    public bool AddMarketData { get; set; } = true;
    public bool AddEngine { get; set; } = true;
    public bool AddCommands { get; set; } = true;
    public bool AddMcp { get; set; } = true;
    public LogLevel MinimumLogLevel { get; set; } = LogLevel.Warning;
    /// <summary>Wird nach allen AddMusterdepot*-Aufrufen ausgeführt (z. B. Quelle durch Replay ersetzen).</summary>
    public Action<IServiceCollection>? ConfigureServices { get; set; }
}

/// <summary>
/// DI-Container wie in der App, aber mit temporärem Datenverzeichnis (<c>MUSTERDEPOT_DATA_DIR</c>), fester Uhr,
/// fortlaufenden IDs, festem Zufall und Fake-Marktdaten.
/// <para>Die Umgebungsvariable gilt prozessweit; deshalb existiert höchstens ein TestHost gleichzeitig
/// (weitere warten in <see cref="Create"/>). Immer mit <c>await using</c> verwenden.</para>
/// </summary>
public sealed class TestHost : IAsyncDisposable
{
    public const string DataDirVariable = "MUSTERDEPOT_DATA_DIR";
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly string? _previousDataDir;
    private readonly ServiceProvider _provider;
    private bool _disposed;

    private TestHost(ServiceProvider provider, string dataDirectory, ManualClock clock, string? previousDataDir)
    {
        _provider = provider;
        DataDirectory = dataDirectory;
        Clock = clock;
        _previousDataDir = previousDataDir;
    }

    public IServiceProvider Services => _provider;
    public string DataDirectory { get; }
    public ManualClock Clock { get; }

    public T Get<T>() where T : notnull => _provider.GetRequiredService<T>();
    public T? TryGet<T>() => _provider.GetService<T>();

    public static TestHost Create(Action<TestHostOptions>? configure = null)
    {
        var options = new TestHostOptions();
        configure?.Invoke(options);

        if (!Gate.Wait(TimeSpan.FromMinutes(2)))
            throw new TimeoutException("Ein anderer TestHost wurde nicht freigegeben (fehlendes await using?).");
        var previous = Environment.GetEnvironmentVariable(DataDirVariable);
        string? dataDir = null;
        try
        {
            dataDir = TestPaths.NewTempDirectory("musterdepot-host");
            Environment.SetEnvironmentVariable(DataDirVariable, dataDir);

            var clock = new ManualClock(options.StartUtc);
            var services = new ServiceCollection();
            services.AddLogging(b => b.SetMinimumLevel(options.MinimumLogLevel));
            if (options.AddPersistence) services.AddMusterdepotPersistence(AppPaths.DatabasePath);
            if (options.AddMarketData) services.AddMusterdepotMarketData(options.DataSources);
            if (options.AddEngine) services.AddMusterdepotEngine();
            if (options.AddCommands) services.AddMusterdepotCommands();
            if (options.AddMcp) services.AddMusterdepotMcp();

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
            return new TestHost(provider, dataDir, clock, previous);
        }
        catch
        {
            Environment.SetEnvironmentVariable(DataDirVariable, previous);
            if (dataDir is not null) TestPaths.TryDeleteDirectory(dataDir);
            Gate.Release();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            await _provider.DisposeAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable(DataDirVariable, _previousDataDir);
            TestPaths.TryDeleteDirectory(DataDirectory);
            Gate.Release();
        }
    }
}
