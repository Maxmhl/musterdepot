using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Musterdepot.Persistence.Entities;

namespace Musterdepot.Persistence;

/// <summary>
/// Zugang zur SQLite-Datei: erzeugt Kontexte (je Vorgang ein eigener, daher thread-sicher), legt das Schema an,
/// schaltet WAL ein und serialisiert Schreibvorgänge über <see cref="WriteLock"/>.
/// <para>
/// Schema: <c>EnsureCreated</c> legt alle Tabellen an, wenn die Datei neu ist. Die Tabelle <c>SchemaInfo</c>
/// (eine Zeile, Id = 1) hält die Schema-Version (<see cref="CurrentSchemaVersion"/>). Beim Öffnen gilt:
/// fehlt die Zeile → wird mit der aktuellen Version angelegt; ältere Version → Migrationsschritte in
/// <see cref="Migrate"/> (je Version ein Schritt mit SQL) und Version hochsetzen; neuere Version → Warnung
/// (Datei stammt von einer neueren Programmversion), Zugriff bleibt erlaubt.
/// </para>
/// </summary>
public sealed class MusterdepotDatabase : IDbContextFactory<MusterdepotDbContext>, IDisposable
{
    /// <summary>Aktuelle Schema-Version. Bei Schemaänderung erhöhen und Schritt in <see cref="Migrate"/> ergänzen.</summary>
    public const int CurrentSchemaVersion = 1;

    private readonly DbContextOptions<MusterdepotDbContext> _options;
    private readonly ILogger _logger;
    private readonly Lock _initLock = new();
    private volatile bool _initialized;

    public MusterdepotDatabase(string databasePath, ILogger<MusterdepotDatabase>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        var dir = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = 30,
            Pooling = true,
        }.ToString();
        _options = new DbContextOptionsBuilder<MusterdepotDbContext>()
            .UseSqlite(ConnectionString)
            .Options;
    }

    public string DatabasePath { get; }
    public string ConnectionString { get; }

    /// <summary>Serialisiert alle Schreibvorgänge (SQLite erlaubt nur einen Schreiber).</summary>
    public SemaphoreSlim WriteLock { get; } = new(1, 1);

    public MusterdepotDbContext CreateDbContext()
    {
        EnsureInitialized();
        return new MusterdepotDbContext(_options);
    }

    /// <summary>Legt Schema an, aktiviert WAL und prüft die Schema-Version (einmalig).</summary>
    public void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_initLock)
        {
            if (_initialized) return;
            using var db = new MusterdepotDbContext(_options);
            db.Database.EnsureCreated();
            db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
            db.Database.ExecuteSqlRaw("PRAGMA synchronous=NORMAL;");
            var info = db.SchemaInfo.SingleOrDefault(x => x.Id == 1);
            var now = DateTimeOffset.UtcNow;
            if (info is null)
            {
                db.SchemaInfo.Add(new SchemaInfoEntity
                {
                    Id = 1, Version = CurrentSchemaVersion, CreatedUtc = now, UpdatedUtc = now,
                    AppVersion = typeof(MusterdepotDatabase).Assembly.GetName().Version?.ToString(),
                });
                db.SaveChanges();
            }
            else if (info.Version < CurrentSchemaVersion)
            {
                Migrate(db, info.Version);
                info.Version = CurrentSchemaVersion;
                info.UpdatedUtc = now;
                db.SaveChanges();
            }
            else if (info.Version > CurrentSchemaVersion)
            {
                _logger.LogWarning("Datenbank {Path} hat Schema-Version {Version}, dieses Programm kennt {Current}.",
                    DatabasePath, info.Version, CurrentSchemaVersion);
            }
            _initialized = true;
        }
    }

    /// <summary>Gelesene Schema-Version (für Diagnose/Tests).</summary>
    public int GetSchemaVersion()
    {
        using var db = CreateDbContext();
        return db.SchemaInfo.AsNoTracking().Single(x => x.Id == 1).Version;
    }

    /// <summary>Aktueller Journal-Modus (erwartet "wal").</summary>
    public string GetJournalMode()
    {
        EnsureInitialized();
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        return Convert.ToString(cmd.ExecuteScalar()) ?? "";
    }

    private void Migrate(MusterdepotDbContext db, int fromVersion)
    {
        // Je Version ein Schritt, z. B.:
        // if (fromVersion < 2) db.Database.ExecuteSqlRaw("ALTER TABLE Orders ADD COLUMN Foo TEXT NULL;");
        _logger.LogInformation("Schema-Migration von Version {From} auf {To}.", fromVersion, CurrentSchemaVersion);
    }

    public void Dispose() => WriteLock.Dispose();
}
