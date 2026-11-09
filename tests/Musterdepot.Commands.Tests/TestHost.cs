using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Musterdepot.Commands.Tests.Fakes;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Commands;
using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;
using Musterdepot.Persistence;

namespace Musterdepot.Commands.Tests;

/// <summary>Temporäre SQLite-Datei im eigenen Ordner, wird beim Dispose gelöscht.</summary>
public sealed class TempDb : IDisposable
{
    public TempDb()
    {
        Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "musterdepot-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        Path = System.IO.Path.Combine(Directory, "test.db");
    }

    public string Directory { get; }
    public string Path { get; }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { System.IO.Directory.Delete(Directory, recursive: true); } catch { /* egal */ }
    }
}

/// <summary>DI-Aufbau mit echter Persistence, Fake-Engine und Fake-Hub.</summary>
public sealed class TestHost : IAsyncDisposable
{
    public const string Acc = "main";
    public const string AutoAi = "auto-ai";
    public const string ConfirmAi = "confirm-ai";
    public const string ReadOnlyAi = "ro-ai";
    public const string SettingsAi = "settings-ai";

    private readonly TempDb _db;
    private bool _ownsDb = true;

    private TestHost(TempDb db, ServiceProvider services, FakeTradingEngine engine, FakeMarketDataHub hub, ManualClock clock, RecordingNotifier notifier)
    {
        _db = db;
        Services = services;
        Engine = engine;
        Hub = hub;
        Clock = clock;
        Notifier = notifier;
    }

    public ServiceProvider Services { get; }
    public FakeTradingEngine Engine { get; }
    public FakeMarketDataHub Hub { get; }
    public ManualClock Clock { get; }
    public RecordingNotifier Notifier { get; }
    public string DatabasePath => _db.Path;
    public ICommandBus Bus => Services.GetRequiredService<ICommandBus>();
    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    public static IReadOnlyList<AiClientConfig> DefaultClients { get; } =
    [
        new() { ClientId = AutoAi, Name = "Auto-KI", Mode = ClientMode.Autonomous, Scopes = ClientScopes.Read | ClientScopes.Trade },
        new() { ClientId = ConfirmAi, Name = "Confirm-KI", Mode = ClientMode.Confirm, Scopes = ClientScopes.Read | ClientScopes.Trade | ClientScopes.Settings },
        new() { ClientId = ReadOnlyAi, Name = "Lese-KI", Mode = ClientMode.ReadOnly, Scopes = ClientScopes.Read },
        new() { ClientId = SettingsAi, Name = "Settings-KI", Mode = ClientMode.Autonomous, Scopes = ClientScopes.All },
    ];

    public static async Task<TestHost> CreateAsync(
        bool openAccount = true,
        TimeSpan? approvalTimeout = null,
        Func<AppSettings, AppSettings>? configure = null,
        RiskConfig? risk = null,
        TempDb? db = null)
    {
        var ownsDb = db is null;
        db ??= new TempDb();
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero));
        var engine = new FakeTradingEngine(clock);
        var hub = new FakeMarketDataHub();
        var notifier = new RecordingNotifier();

        var services = new ServiceCollection();
        services.AddSingleton(new CommandOptions
        {
            ApprovalTimeout = approvalTimeout ?? TimeSpan.FromSeconds(10),
            ClockTickInterval = TimeSpan.FromMilliseconds(20),
            QuoteThrottleInterval = TimeSpan.FromMilliseconds(50),
        });
        services.AddSingleton<IClock>(clock);
        services.AddSingleton<IIdGenerator>(new SequentialIdGenerator());
        services.AddSingleton<IUserNotifier>(notifier);
        services.AddSingleton<ITradingEngine>(engine);
        services.AddSingleton<IMarketDataHub>(hub);
        services.AddMusterdepotPersistence(db.Path);
        services.AddMusterdepotCommands();
        var sp = services.BuildServiceProvider();

        var settings = new AppSettings { AiClients = DefaultClients, Risk = risk ?? RiskConfig.Default };
        if (configure is not null) settings = configure(settings);
        await sp.GetRequiredService<ISettingsStore>().SaveAsync(settings);
        await sp.GetRequiredService<SettingsService>().LoadAsync();

        if (openAccount)
            engine.Execute(new OpenAccountRequest { AccountId = Acc, Name = "Test", InitialCash = 100_000m, Risk = settings.Risk });

        return new TestHost(db, sp, engine, hub, clock, notifier) { _ownsDb = ownsDb };
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (_ownsDb) _db.Dispose();
    }

    public static PlaceOrderCommand Buy(string clientId = "ui", string? key = null, string symbol = "BTC/EUR", decimal qty = 0.1m, bool dryRun = false) => new()
    {
        Source = clientId == "ui" ? CommandSource.Human : CommandSource.Ai,
        ClientId = clientId,
        IdempotencyKey = key ?? Guid.NewGuid().ToString("N"),
        Symbol = symbol,
        Side = OrderSide.Buy,
        Type = OrderType.Limit,
        LimitPrice = 60_000m,
        Quantity = qty,
        DryRun = dryRun,
    };

    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Bedingung nicht rechtzeitig erfüllt.");
            await Task.Delay(10);
        }
    }

    public static async Task WaitUntilAsync(Func<Task<bool>> condition, int timeoutMs = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!await condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Bedingung nicht rechtzeitig erfüllt.");
            await Task.Delay(10);
        }
    }
}
