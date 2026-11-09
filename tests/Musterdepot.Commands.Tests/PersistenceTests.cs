using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Commands;
using Musterdepot.Core.Domain;
using Musterdepot.Core.Events;
using Musterdepot.Core.Serialization;
using Musterdepot.Persistence;

namespace Musterdepot.Commands.Tests;

public sealed class PersistenceTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);
    private readonly TempDb _tmp = new();
    private readonly MusterdepotDatabase _db;

    public PersistenceTests() => _db = new MusterdepotDatabase(_tmp.Path);

    public void Dispose()
    {
        _db.Dispose();
        _tmp.Dispose();
    }

    private static Order MakeOrder(string id, decimal qty, decimal? limit = null) => new()
    {
        OrderId = id, ClientOrderId = "c-" + id, AccountId = "main", Symbol = "BTC/EUR", Side = OrderSide.Buy, Type = OrderType.Limit,
        Quantity = qty, LimitPrice = limit, Source = CommandSource.Ai, ClientName = "KI", CreatedUtc = T0, Status = OrderStatus.PendingNew,
    };

    private static List<DomainEvent> SampleEvents()
    {
        const decimal precise = 1234567890.123456789012345678m; // 28 signifikante Stellen
        const decimal tiny = 0.0000000000000000000000000001m;
        return
        [
            new AccountOpened { AccountId = "main", Sequence = 1, TimestampUtc = T0, Name = "Haupt", InitialCash = precise, EventId = "e1" },
            new CashDeposited { AccountId = "main", Sequence = 2, TimestampUtc = T0.AddSeconds(1), Amount = 1.10m, Note = "Test" },
            new OrderSubmitted { AccountId = "main", Sequence = 3, TimestampUtc = T0.AddSeconds(2), Order = MakeOrder("o1", 2m, 60_000.123456789m) },
            new OrderAccepted { AccountId = "main", Sequence = 4, TimestampUtc = T0.AddSeconds(2), OrderId = "o1" },
            new OrderFilled
            {
                AccountId = "main", Sequence = 5, TimestampUtc = T0.AddSeconds(3), ResultingStatus = OrderStatus.PartiallyFilled,
                Fill = new Fill { FillId = "f1", OrderId = "o1", AccountId = "main", Symbol = "BTC/EUR", Side = OrderSide.Buy, Quantity = 1.5m, Price = 50_000m, Fee = tiny, TimestampUtc = T0.AddSeconds(3), Source = CommandSource.Ai },
            },
            new OrderFilled
            {
                AccountId = "main", Sequence = 6, TimestampUtc = T0.AddSeconds(4), ResultingStatus = OrderStatus.Filled,
                Fill = new Fill { FillId = "f2", OrderId = "o1", AccountId = "main", Symbol = "BTC/EUR", Side = OrderSide.Buy, Quantity = 0.5m, Price = 52_000m, Fee = 2m, TimestampUtc = T0.AddSeconds(4), Source = CommandSource.Ai },
            },
            new OrderSubmitted { AccountId = "main", Sequence = 7, TimestampUtc = T0.AddSeconds(5), Order = MakeOrder("o2", 1m) with { Side = OrderSide.Sell, Type = OrderType.Market } },
            new OrderFilled
            {
                AccountId = "main", Sequence = 8, TimestampUtc = T0.AddSeconds(6), ResultingStatus = OrderStatus.Filled,
                Fill = new Fill { FillId = "f3", OrderId = "o2", AccountId = "main", Symbol = "BTC/EUR", Side = OrderSide.Sell, Quantity = 1m, Price = 55_000m, Fee = 1m, RealizedPnl = 4_999m, TimestampUtc = T0.AddSeconds(6), Source = CommandSource.Ai },
            },
            new OrderSubmitted { AccountId = "main", Sequence = 9, TimestampUtc = T0.AddSeconds(7), Order = MakeOrder("o3", 3m, 40_000m) },
            new OrderCancelled { AccountId = "main", Sequence = 10, TimestampUtc = T0.AddSeconds(8), OrderId = "o3", Reason = "user" },
            new KillSwitchEngaged { AccountId = "main", Sequence = 11, TimestampUtc = T0.AddSeconds(9), Source = CommandSource.Ai, Reason = "Test" },
            new AccountOpened { AccountId = "second", Sequence = 1, TimestampUtc = T0, Name = "Zweit", InitialCash = 10m },
        ];
    }

    [Fact]
    public async Task EventStore_Roundtrip_IsLossless_IncludingDecimals()
    {
        var store = new SqliteEventStore(_db);
        var events = SampleEvents();
        await store.AppendAsync(events);

        var read = await store.ReadAllAsync();
        Assert.Equal(events.Count, read.Count);
        for (var i = 0; i < events.Count; i++)
        {
            Assert.Equal(events[i].GetType(), read[i].GetType());
            Assert.Equal(JsonDefaults.SerializeEvent(events[i]), JsonDefaults.SerializeEvent(read[i]));
        }
        Assert.Equal(1234567890.123456789012345678m, ((AccountOpened)read[0]).InitialCash);
        Assert.Equal("1.10", ((CashDeposited)read[1]).Amount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(0.0000000000000000000000000001m, ((OrderFilled)read[4]).Fill.Fee);

        var onlyMain = await store.ReadAllAsync("main");
        Assert.Equal(events.Count - 1, onlyMain.Count);
        Assert.All(onlyMain, e => Assert.Equal("main", e.AccountId));

        // Erneut öffnen (neue Datenbank-Instanz) liefert dasselbe.
        using var db2 = new MusterdepotDatabase(_tmp.Path);
        var again = await new SqliteEventStore(db2).ReadAllAsync();
        Assert.Equal(events.Count, again.Count);
    }

    [Fact]
    public async Task EventStore_SkipsAlreadyStoredSequences()
    {
        var store = new SqliteEventStore(_db);
        var events = SampleEvents();
        await store.AppendAsync(events.Take(3).ToList());
        await store.AppendAsync(events); // überlappend
        Assert.Equal(events.Count, (await store.ReadAllAsync()).Count);
    }

    [Fact]
    public async Task EventStore_UpdatesProjections()
    {
        await new SqliteEventStore(_db).AppendAsync(SampleEvents());
        await using var ctx = _db.CreateDbContext();

        var acc = await ctx.Accounts.SingleAsync(a => a.AccountId == "main");
        Assert.True(acc.KillSwitchEngaged);
        Assert.Equal(11, acc.LastSequence);
        var expectedCash = 1234567890.123456789012345678m + 1.10m - 75_000m - 0.0000000000000000000000000001m - 26_000m - 2m + 55_000m - 1m;
        Assert.Equal(expectedCash, acc.Cash);
        Assert.Equal(4_999m, acc.RealizedPnl);

        var orders = await ctx.Orders.OrderBy(o => o.OrderId).ToListAsync();
        Assert.Equal(3, orders.Count);
        Assert.Equal("Filled", orders[0].Status);
        Assert.Equal(2m, orders[0].FilledQuantity);
        Assert.Equal(50_500m, orders[0].AveragePrice);
        Assert.Equal(60_000.123456789m, orders[0].LimitPrice);
        Assert.Equal("Cancelled", orders[2].Status);

        Assert.Equal(3, await ctx.Fills.CountAsync());
        var pos = await ctx.Positions.SingleAsync(p => p.AccountId == "main" && p.Symbol == "BTC/EUR");
        Assert.Equal(1m, pos.Quantity); // 2 gekauft, 1 verkauft (FIFO: 0,5 aus erstem Lot + 0,5 aus zweitem)
        Assert.Equal(4_999m, pos.RealizedPnl);

        var cash = await ctx.CashEntries.Where(c => c.AccountId == "main").ToListAsync();
        Assert.Equal(acc.Cash, cash.Sum(c => c.Amount));
        Assert.Contains(cash, c => c.Kind == "fee" && c.Amount == -0.0000000000000000000000000001m);
        Assert.Equal(2, await ctx.Accounts.CountAsync());
    }

    [Fact]
    public async Task Money_IsStoredAsText()
    {
        await new SqliteEventStore(_db).AppendAsync(SampleEvents());
        await using var ctx = _db.CreateDbContext();
        var conn = ctx.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT typeof(InitialCash), InitialCash FROM Accounts WHERE AccountId = 'main'";
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        Assert.Equal("text", r.GetString(0));
        Assert.Equal("1234567890.123456789012345678", r.GetString(1));
    }

    [Fact]
    public async Task CandleStore_Upsert_Get_Prune()
    {
        var clock = new ManualClock(T0);
        var store = new SqliteCandleStore(_db, clock);
        Candle C(Timeframe tf, DateTimeOffset t, decimal close) => new()
        {
            Symbol = "BTC/EUR", Timeframe = tf, OpenTimeUtc = t, Open = 1, High = 2, Low = 0.5m, Close = close, Volume = 0.123456789012345678m,
        };

        await store.UpsertAsync([C(Timeframe.M1, T0.AddMinutes(-2), 10m), C(Timeframe.M1, T0.AddMinutes(-1), 11m)]);
        await store.UpsertAsync([C(Timeframe.M1, T0.AddMinutes(-1), 12m)]); // Update
        var m1 = await store.GetAsync("BTC/EUR", Timeframe.M1, null, null);
        Assert.Equal(2, m1.Count);
        Assert.Equal(12m, m1[1].Close);
        Assert.Equal(0.123456789012345678m, m1[0].Volume);
        Assert.True(m1[0].OpenTimeUtc < m1[1].OpenTimeUtc);

        var range = await store.GetAsync("BTC/EUR", Timeframe.M1, T0.AddMinutes(-1), T0);
        Assert.Single(range);

        await store.UpsertAsync([C(Timeframe.M1, T0.AddDays(-100), 1m), C(Timeframe.H1, T0.AddDays(-400), 1m)]);
        await store.PruneAsync(TimeSpan.FromDays(90));
        Assert.Equal(2, (await store.GetAsync("BTC/EUR", Timeframe.M1, null, null)).Count);
        Assert.Single(await store.GetAsync("BTC/EUR", Timeframe.H1, null, null)); // längere Zeitrahmen bleiben
    }

    [Fact]
    public async Task SettingsStore_DefaultWhenEmpty_AndRoundtrip()
    {
        var store = new SqliteSettingsStore(_db);
        var def = await store.LoadAsync();
        Assert.Equal(new AppSettings().McpPort, def.McpPort);
        Assert.Equal(100_000m, def.InitialCash);

        var s = def with
        {
            McpPort = 50000, InitialCash = 12_345.6789m,
            Risk = RiskConfig.Default with { AllowedSymbols = ["BTC/EUR"], MaxDailyLoss = 0.5m },
            AiClients = [new AiClientConfig { ClientId = "a", Name = "A", Mode = ClientMode.Autonomous }],
        };
        await store.SaveAsync(s);
        await store.SaveAsync(s with { Theme = "dark" });
        var back = await store.LoadAsync();
        Assert.Equal(50000, back.McpPort);
        Assert.Equal("dark", back.Theme);
        Assert.Equal(12_345.6789m, back.InitialCash);
        Assert.Equal(["BTC/EUR"], back.Risk.AllowedSymbols!);
        Assert.Equal(ClientMode.Autonomous, back.AiClients.Single().Mode);
    }

    [Fact]
    public async Task AuditLog_AppendRaisesEvent_AndQueryFilters()
    {
        var log = new SqliteAuditLog(_db);
        var raised = new List<AuditEntry>();
        log.EntryAppended += (_, e) => raised.Add(e);
        for (var i = 0; i < 5; i++)
        {
            await log.AppendAsync(new AuditEntry
            {
                AuditId = $"a{i}", TimestampUtc = T0.AddMinutes(i), Source = i % 2 == 0 ? CommandSource.Ai : CommandSource.Human,
                ClientId = i % 2 == 0 ? "ki" : "ui", Action = "PlaceOrderCommand", ParametersJson = "{}", Result = "Executed",
                ApprovedBy = i == 2 ? "Oberfläche" : null, Duration = TimeSpan.FromMilliseconds(12.5),
            });
        }
        Assert.Equal(5, raised.Count);
        var all = await log.QueryAsync();
        Assert.Equal(["a4", "a3", "a2", "a1", "a0"], all.Select(a => a.AuditId));
        Assert.Equal(3, (await log.QueryAsync(clientId: "ki")).Count);
        Assert.Equal(2, (await log.QueryAsync(fromUtc: T0.AddMinutes(3))).Count);
        Assert.Equal(2, (await log.QueryAsync(limit: 2)).Count);
        var a2 = all.Single(a => a.AuditId == "a2");
        Assert.Equal("Oberfläche", a2.ApprovedBy);
        Assert.Equal(TimeSpan.FromMilliseconds(12.5), a2.Duration);
        Assert.Equal(CommandSource.Ai, a2.Source);
    }

    [Fact]
    public async Task WatchlistStore_SaveGetDelete()
    {
        var store = new SqliteWatchlistStore(_db);
        await store.SaveAsync(new Watchlist { WatchlistId = "w1", Name = "Krypto", Symbols = ["BTC/EUR", "ETH/EUR"], CreatedUtc = T0 });
        await store.SaveAsync(new Watchlist { WatchlistId = "w2", Name = "Aktien", Symbols = ["DE0007164600"], CreatedUtc = T0.AddMinutes(1) });
        await store.SaveAsync(new Watchlist { WatchlistId = "w1", Name = "Krypto 2", Symbols = ["SOL/EUR", "BTC/EUR"], CreatedUtc = T0 });
        var all = await store.GetAllAsync();
        Assert.Equal(2, all.Count);
        Assert.Equal("Krypto 2", all[0].Name);
        Assert.Equal(["SOL/EUR", "BTC/EUR"], all[0].Symbols);
        await store.DeleteAsync("w1");
        Assert.Equal("w2", (await store.GetAllAsync()).Single().WatchlistId);
        await using var ctx = _db.CreateDbContext();
        Assert.Equal(1, await ctx.WatchlistItems.CountAsync());
    }

    [Fact]
    public async Task AlertStore_SaveUpdateDelete()
    {
        var store = new SqliteAlertStore(_db);
        var alert = new PriceAlert { AlertId = "al1", Symbol = "BTC/EUR", Condition = AlertCondition.PriceAbove, Threshold = 70_000.5m, CreatedUtc = T0, Note = "n" };
        await store.SaveAsync(alert);
        await store.SaveAsync(alert with { TriggeredUtc = T0.AddHours(1) });
        var back = (await store.GetAllAsync()).Single();
        Assert.False(back.IsActive);
        Assert.Equal(70_000.5m, back.Threshold);
        Assert.Equal(T0.AddHours(1), back.TriggeredUtc);
        await store.DeleteAsync("al1");
        Assert.Empty(await store.GetAllAsync());
    }

    [Fact]
    public async Task InstrumentStore_Upsert()
    {
        var store = new SqliteInstrumentStore(_db);
        await store.UpsertAsync([new Instrument { Symbol = "BTC/EUR", Name = "Bitcoin", AssetClass = AssetClass.Crypto, QuoteCurrency = "EUR", QuantityStep = 0.00000001m }]);
        await store.UpsertAsync([new Instrument { Symbol = "BTC/EUR", Name = "Bitcoin (Kraken)", AssetClass = AssetClass.Crypto, QuoteCurrency = "EUR", QuantityStep = 0.00000001m }]);
        var i = (await store.GetAllAsync()).Single();
        Assert.Equal("Bitcoin (Kraken)", i.Name);
        Assert.Equal(0.00000001m, i.QuantityStep);
    }

    [Fact]
    public async Task Database_UsesWal_AndSchemaVersion()
    {
        Assert.Equal("wal", _db.GetJournalMode());
        Assert.Equal(MusterdepotDatabase.CurrentSchemaVersion, _db.GetSchemaVersion());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AddMusterdepotPersistence_RegistersAllStores()
    {
        using var tmp = new TempDb();
        var services = new ServiceCollection().AddMusterdepotPersistence(tmp.Path);
        await using var sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetRequiredService<IEventStore>());
        Assert.NotNull(sp.GetRequiredService<ICandleStore>());
        Assert.NotNull(sp.GetRequiredService<IAuditLog>());
        Assert.NotNull(sp.GetRequiredService<ISettingsStore>());
        Assert.NotNull(sp.GetRequiredService<IWatchlistStore>());
        Assert.NotNull(sp.GetRequiredService<IAlertStore>());
        Assert.NotNull(sp.GetRequiredService<IInstrumentStore>());
        Assert.Same(sp.GetRequiredService<IAuditLog>(), sp.GetRequiredService<IAuditLog>());
        await sp.GetRequiredService<IEventStore>().AppendAsync(SampleEvents());
        Assert.True(File.Exists(tmp.Path));
    }

    [Fact]
    public async Task ConcurrentWrites_AreSerialized()
    {
        var log = new SqliteAuditLog(_db);
        var candles = new SqliteCandleStore(_db);
        var tasks = Enumerable.Range(0, 40).Select(i => i % 2 == 0
            ? (Task)log.AppendAsync(new AuditEntry { AuditId = $"x{i}", TimestampUtc = T0, Source = CommandSource.Human, ClientId = "ui", Action = "A", Result = "Executed" })
            : candles.UpsertAsync([new Candle { Symbol = "S", Timeframe = Timeframe.M1, OpenTimeUtc = T0.AddMinutes(i), Open = 1, High = 1, Low = 1, Close = 1 }]));
        await Task.WhenAll(tasks);
        Assert.Equal(20, (await log.QueryAsync()).Count);
        Assert.Equal(20, (await candles.GetAsync("S", Timeframe.M1, null, null)).Count);
    }
}
