using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Commands;
using Musterdepot.Core.Domain;
using Musterdepot.Core.Serialization;
using Musterdepot.Persistence.Entities;

namespace Musterdepot.Persistence;

/// <summary>Protokoll (Tabelle AiActions). <see cref="QueryAsync"/> liefert die neuesten Einträge zuerst.</summary>
public sealed class SqliteAuditLog(MusterdepotDatabase database) : IAuditLog
{
    public event EventHandler<AuditEntry>? EntryAppended;

    public async Task<AuditEntry> AppendAsync(AuditEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrEmpty(entry.AuditId)) entry = entry with { AuditId = $"aud_{Guid.CreateVersion7():N}" };
        await database.WriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = database.CreateDbContext();
            db.AiActions.Add(new AiActionEntity
            {
                AuditId = entry.AuditId,
                TimestampUtc = entry.TimestampUtc,
                Source = entry.Source.ToString(),
                ClientId = entry.ClientId,
                Action = entry.Action,
                ParametersJson = entry.ParametersJson,
                Result = entry.Result,
                Message = entry.Message,
                ApprovedBy = entry.ApprovedBy,
                IdempotencyKey = entry.IdempotencyKey,
                DurationMs = entry.Duration?.TotalMilliseconds,
            });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            database.WriteLock.Release();
        }
        EntryAppended?.Invoke(this, entry);
        return entry;
    }

    public async Task<IReadOnlyList<AuditEntry>> QueryAsync(DateTimeOffset? fromUtc = null, string? clientId = null, int limit = 200, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 10_000);
        await using var db = database.CreateDbContext();
        var q = db.AiActions.AsNoTracking();
        if (fromUtc is { } from) q = q.Where(x => x.TimestampUtc >= from);
        if (!string.IsNullOrEmpty(clientId)) q = q.Where(x => x.ClientId == clientId);
        var rows = await q.OrderByDescending(x => x.TimestampUtc).ThenByDescending(x => x.AuditId).Take(limit).ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(x => new AuditEntry
        {
            AuditId = x.AuditId,
            TimestampUtc = x.TimestampUtc,
            Source = Enum.Parse<CommandSource>(x.Source),
            ClientId = x.ClientId,
            Action = x.Action,
            ParametersJson = x.ParametersJson,
            Result = x.Result,
            Message = x.Message,
            ApprovedBy = x.ApprovedBy,
            IdempotencyKey = x.IdempotencyKey,
            Duration = x.DurationMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null,
        }).ToList();
    }
}

/// <summary>Einstellungen als JSON (Tabelle Settings, Schlüssel "AppSettings"). Leer → Standardwerte.</summary>
public sealed class SqliteSettingsStore(MusterdepotDatabase database) : ISettingsStore
{
    public const string AppSettingsKey = "AppSettings";

    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        await using var db = database.CreateDbContext();
        var row = await db.Settings.AsNoTracking().SingleOrDefaultAsync(x => x.Key == AppSettingsKey, ct).ConfigureAwait(false);
        if (row is null || string.IsNullOrWhiteSpace(row.ValueJson)) return new AppSettings();
        return JsonSerializer.Deserialize<AppSettings>(row.ValueJson, JsonDefaults.Options) ?? new AppSettings();
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var json = JsonSerializer.Serialize(settings, JsonDefaults.Options);
        await database.WriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = database.CreateDbContext();
            var row = await db.Settings.SingleOrDefaultAsync(x => x.Key == AppSettingsKey, ct).ConfigureAwait(false);
            if (row is null)
            {
                row = new SettingEntity { Key = AppSettingsKey };
                db.Settings.Add(row);
            }
            row.ValueJson = json;
            row.UpdatedUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            database.WriteLock.Release();
        }
    }
}

/// <summary>Watchlists mit geordneten Einträgen (Tabellen Watchlists und WatchlistItems).</summary>
public sealed class SqliteWatchlistStore(MusterdepotDatabase database) : IWatchlistStore
{
    public async Task<IReadOnlyList<Watchlist>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = database.CreateDbContext();
        var rows = await db.Watchlists.AsNoTracking().Include(x => x.Items).ToListAsync(ct).ConfigureAwait(false);
        return rows
            .OrderBy(x => x.CreatedUtc).ThenBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => new Watchlist
            {
                WatchlistId = x.WatchlistId,
                Name = x.Name,
                CreatedUtc = x.CreatedUtc,
                Symbols = x.Items.OrderBy(i => i.Position).Select(i => i.Symbol).ToList(),
            }).ToList();
    }

    public async Task SaveAsync(Watchlist watchlist, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(watchlist);
        await database.WriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = database.CreateDbContext();
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            var row = await db.Watchlists.Include(x => x.Items).SingleOrDefaultAsync(x => x.WatchlistId == watchlist.WatchlistId, ct).ConfigureAwait(false);
            if (row is null)
            {
                row = new WatchlistEntity { WatchlistId = watchlist.WatchlistId };
                db.Watchlists.Add(row);
            }
            row.Name = watchlist.Name;
            row.CreatedUtc = watchlist.CreatedUtc;
            db.WatchlistItems.RemoveRange(row.Items);
            row.Items = watchlist.Symbols.Select((s, i) => new WatchlistItemEntity { WatchlistId = watchlist.WatchlistId, Symbol = s, Position = i }).ToList();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            database.WriteLock.Release();
        }
    }

    public async Task DeleteAsync(string watchlistId, CancellationToken ct = default)
    {
        await database.WriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = database.CreateDbContext();
            await db.WatchlistItems.Where(x => x.WatchlistId == watchlistId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.Watchlists.Where(x => x.WatchlistId == watchlistId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            database.WriteLock.Release();
        }
    }
}

/// <summary>Preisalarme (Tabelle Alerts).</summary>
public sealed class SqliteAlertStore(MusterdepotDatabase database) : IAlertStore
{
    public async Task<IReadOnlyList<PriceAlert>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = database.CreateDbContext();
        var rows = await db.Alerts.AsNoTracking().OrderBy(x => x.CreatedUtc).ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(x => new PriceAlert
        {
            AlertId = x.AlertId,
            Symbol = x.Symbol,
            Condition = Enum.Parse<AlertCondition>(x.Condition),
            Threshold = x.Threshold,
            Note = x.Note,
            CreatedUtc = x.CreatedUtc,
            TriggeredUtc = x.TriggeredUtc,
        }).ToList();
    }

    public async Task SaveAsync(PriceAlert alert, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(alert);
        await database.WriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = database.CreateDbContext();
            var row = await db.Alerts.FindAsync([alert.AlertId], ct).ConfigureAwait(false);
            if (row is null)
            {
                row = new AlertEntity { AlertId = alert.AlertId };
                db.Alerts.Add(row);
            }
            row.Symbol = alert.Symbol;
            row.Condition = alert.Condition.ToString();
            row.Threshold = alert.Threshold;
            row.Note = alert.Note;
            row.CreatedUtc = alert.CreatedUtc;
            row.TriggeredUtc = alert.TriggeredUtc;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            database.WriteLock.Release();
        }
    }

    public async Task DeleteAsync(string alertId, CancellationToken ct = default)
    {
        await database.WriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = database.CreateDbContext();
            await db.Alerts.Where(x => x.AlertId == alertId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            database.WriteLock.Release();
        }
    }
}

/// <summary>Instrumente (Tabelle Instruments), Upsert per Symbol.</summary>
public sealed class SqliteInstrumentStore(MusterdepotDatabase database) : IInstrumentStore
{
    public async Task<IReadOnlyList<Instrument>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = database.CreateDbContext();
        var rows = await db.Instruments.AsNoTracking().OrderBy(x => x.Symbol).ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(x => new Instrument
        {
            Symbol = x.Symbol,
            Name = x.Name,
            AssetClass = Enum.Parse<AssetClass>(x.AssetClass),
            QuoteCurrency = x.QuoteCurrency,
            BaseAsset = x.BaseAsset,
            Isin = x.Isin,
            Wkn = x.Wkn,
            TickSize = x.TickSize,
            QuantityStep = x.QuantityStep,
            MinQuantity = x.MinQuantity,
            PrimarySource = x.PrimarySource,
            SourceSymbol = x.SourceSymbol,
        }).ToList();
    }

    public async Task UpsertAsync(IReadOnlyList<Instrument> instruments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instruments);
        if (instruments.Count == 0) return;
        await database.WriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = database.CreateDbContext();
            var now = DateTimeOffset.UtcNow;
            foreach (var i in instruments)
            {
                var row = await db.Instruments.FindAsync([i.Symbol], ct).ConfigureAwait(false);
                if (row is null)
                {
                    row = new InstrumentEntity { Symbol = i.Symbol };
                    db.Instruments.Add(row);
                }
                row.Name = i.Name;
                row.AssetClass = i.AssetClass.ToString();
                row.QuoteCurrency = i.QuoteCurrency;
                row.BaseAsset = i.BaseAsset;
                row.Isin = i.Isin;
                row.Wkn = i.Wkn;
                row.TickSize = i.TickSize;
                row.QuantityStep = i.QuantityStep;
                row.MinQuantity = i.MinQuantity;
                row.PrimarySource = i.PrimarySource;
                row.SourceSymbol = i.SourceSymbol;
                row.UpdatedUtc = now;
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            database.WriteLock.Release();
        }
    }
}
