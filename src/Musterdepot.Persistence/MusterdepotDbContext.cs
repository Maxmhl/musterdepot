using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Musterdepot.Persistence.Entities;

namespace Musterdepot.Persistence;

/// <summary>Wandelt decimal verlustfrei (inkl. Nachkommastellen/Skalierung) in invarianten Text um.</summary>
public sealed class DecimalToInvariantStringConverter() : ValueConverter<decimal, string>(
    v => ToText(v),
    v => FromText(v))
{
    public static string ToText(decimal v) => v.ToString(CultureInfo.InvariantCulture);
    public static decimal FromText(string v) => decimal.Parse(v, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture);
}

/// <summary>Speichert Zeitpunkte als UTC-Text "yyyy-MM-ddTHH:mm:ss.fffffffZ" (lexikografisch = zeitlich sortierbar).</summary>
public sealed class UtcDateTimeOffsetToStringConverter() : ValueConverter<DateTimeOffset, string>(
    v => ToText(v),
    v => FromText(v))
{
    public const string Format = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
    public static string ToText(DateTimeOffset v) => v.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture);
    public static DateTimeOffset FromText(string v) =>
        new(DateTime.SpecifyKind(DateTime.ParseExact(v, Format, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal), DateTimeKind.Utc));
}

/// <summary>EF-Core-Kontext der Musterdepot-Datenbank (eine SQLite-Datei).</summary>
public sealed class MusterdepotDbContext(DbContextOptions<MusterdepotDbContext> options) : DbContext(options)
{
    public DbSet<SchemaInfoEntity> SchemaInfo => Set<SchemaInfoEntity>();
    public DbSet<AccountEntity> Accounts => Set<AccountEntity>();
    public DbSet<InstrumentEntity> Instruments => Set<InstrumentEntity>();
    public DbSet<WatchlistEntity> Watchlists => Set<WatchlistEntity>();
    public DbSet<WatchlistItemEntity> WatchlistItems => Set<WatchlistItemEntity>();
    public DbSet<OrderEntity> Orders => Set<OrderEntity>();
    public DbSet<FillEntity> Fills => Set<FillEntity>();
    public DbSet<PositionEntity> Positions => Set<PositionEntity>();
    public DbSet<CashEntryEntity> CashEntries => Set<CashEntryEntity>();
    public DbSet<CandleEntity> Candles => Set<CandleEntity>();
    public DbSet<AiActionEntity> AiActions => Set<AiActionEntity>();
    public DbSet<SettingEntity> Settings => Set<SettingEntity>();
    public DbSet<EventEntity> Events => Set<EventEntity>();
    public DbSet<AlertEntity> Alerts => Set<AlertEntity>();

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        b.Properties<decimal>().HaveConversion<DecimalToInvariantStringConverter>().HaveColumnType("TEXT");
        b.Properties<DateTimeOffset>().HaveConversion<UtcDateTimeOffsetToStringConverter>().HaveColumnType("TEXT");
    }

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<SchemaInfoEntity>(e =>
        {
            e.ToTable("SchemaInfo");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
        });

        m.Entity<AccountEntity>(e =>
        {
            e.ToTable("Accounts");
            e.HasKey(x => x.AccountId);
        });

        m.Entity<InstrumentEntity>(e =>
        {
            e.ToTable("Instruments");
            e.HasKey(x => x.Symbol);
            e.HasIndex(x => x.Isin);
        });

        m.Entity<WatchlistEntity>(e =>
        {
            e.ToTable("Watchlists");
            e.HasKey(x => x.WatchlistId);
            e.HasMany(x => x.Items).WithOne().HasForeignKey(i => i.WatchlistId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<WatchlistItemEntity>(e =>
        {
            e.ToTable("WatchlistItems");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.WatchlistId, x.Position });
        });

        m.Entity<OrderEntity>(e =>
        {
            e.ToTable("Orders");
            e.HasKey(x => x.OrderId);
            e.HasIndex(x => new { x.AccountId, x.ClientOrderId });
            e.HasIndex(x => new { x.AccountId, x.Status });
        });

        m.Entity<FillEntity>(e =>
        {
            e.ToTable("Fills");
            e.HasKey(x => x.FillId);
            e.HasIndex(x => new { x.AccountId, x.TimestampUtc });
            e.HasIndex(x => x.OrderId);
        });

        m.Entity<PositionEntity>(e =>
        {
            e.ToTable("Positions");
            e.HasKey(x => new { x.AccountId, x.Symbol });
        });

        m.Entity<CashEntryEntity>(e =>
        {
            e.ToTable("CashEntries");
            e.HasKey(x => x.EntryId);
            e.HasIndex(x => new { x.AccountId, x.TimestampUtc });
        });

        m.Entity<CandleEntity>(e =>
        {
            e.ToTable("Candles");
            e.HasKey(x => new { x.Symbol, x.Timeframe, x.OpenTimeUtc });
            e.HasIndex(x => new { x.Timeframe, x.OpenTimeUtc });
        });

        m.Entity<AiActionEntity>(e =>
        {
            e.ToTable("AiActions");
            e.HasKey(x => x.AuditId);
            e.HasIndex(x => x.TimestampUtc);
            e.HasIndex(x => new { x.ClientId, x.TimestampUtc });
        });

        m.Entity<SettingEntity>(e =>
        {
            e.ToTable("Settings");
            e.HasKey(x => x.Key);
        });

        m.Entity<EventEntity>(e =>
        {
            e.ToTable("Events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => new { x.AccountId, x.Sequence });
        });

        m.Entity<AlertEntity>(e =>
        {
            e.ToTable("Alerts");
            e.HasKey(x => x.AlertId);
            e.HasIndex(x => x.Symbol);
        });
    }
}
