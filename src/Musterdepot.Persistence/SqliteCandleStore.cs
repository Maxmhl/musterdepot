using Microsoft.EntityFrameworkCore;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;
using Musterdepot.Persistence.Entities;

namespace Musterdepot.Persistence;

/// <summary>
/// Kerzen-Speicher. Schlüssel (Symbol, Timeframe, OpenTimeUtc) – Upsert überschreibt vorhandene Kerzen.
/// <see cref="PruneAsync"/> löscht nur 1-Minuten-Kerzen; längere Zeitrahmen bleiben unbegrenzt.
/// </summary>
public sealed class SqliteCandleStore(MusterdepotDatabase database, IClock? clock = null) : ICandleStore
{
    private readonly IClock _clock = clock ?? SystemClock.Instance;

    /// <summary>Standard-Aufbewahrung der 1-Minuten-Kerzen.</summary>
    public static readonly TimeSpan DefaultMinuteRetention = TimeSpan.FromDays(90);

    public async Task UpsertAsync(IReadOnlyList<Candle> candles, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candles);
        if (candles.Count == 0) return;
        await database.WriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = database.CreateDbContext();
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            foreach (var c in candles)
            {
                var tf = c.Timeframe.ToString();
                var openTime = c.OpenTimeUtc.ToUniversalTime();
                var e = await db.Candles.FindAsync([c.Symbol, tf, openTime], ct).ConfigureAwait(false);
                if (e is null)
                {
                    e = new CandleEntity { Symbol = c.Symbol, Timeframe = tf, OpenTimeUtc = openTime };
                    db.Candles.Add(e);
                }
                e.Open = c.Open;
                e.High = c.High;
                e.Low = c.Low;
                e.Close = c.Close;
                e.Volume = c.Volume;
                e.TradeCount = c.TradeCount;
                e.Source = c.Source;
                e.IsBackfill = c.IsBackfill;
                e.IsClosed = c.IsClosed;
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            database.WriteLock.Release();
        }
    }

    public async Task<IReadOnlyList<Candle>> GetAsync(string symbol, Timeframe timeframe, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, CancellationToken ct = default)
    {
        await using var db = database.CreateDbContext();
        var tf = timeframe.ToString();
        var q = db.Candles.AsNoTracking().Where(x => x.Symbol == symbol && x.Timeframe == tf);
        if (fromUtc is { } from) q = q.Where(x => x.OpenTimeUtc >= from);
        if (toUtc is { } to) q = q.Where(x => x.OpenTimeUtc <= to);
        var rows = await q.OrderBy(x => x.OpenTimeUtc).ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(e => new Candle
        {
            Symbol = e.Symbol,
            Timeframe = Enum.Parse<Timeframe>(e.Timeframe),
            OpenTimeUtc = e.OpenTimeUtc,
            Open = e.Open,
            High = e.High,
            Low = e.Low,
            Close = e.Close,
            Volume = e.Volume,
            TradeCount = e.TradeCount,
            Source = e.Source,
            IsBackfill = e.IsBackfill,
            IsClosed = e.IsClosed,
        }).ToList();
    }

    public async Task PruneAsync(TimeSpan minuteRetention, CancellationToken ct = default)
    {
        var cutoff = _clock.UtcNow - minuteRetention;
        var m1 = nameof(Timeframe.M1);
        await database.WriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = database.CreateDbContext();
            await db.Candles.Where(x => x.Timeframe == m1 && x.OpenTimeUtc < cutoff).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            database.WriteLock.Release();
        }
    }
}
