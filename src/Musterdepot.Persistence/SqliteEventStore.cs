using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;
using Musterdepot.Core.Events;
using Musterdepot.Core.Serialization;
using Musterdepot.Persistence.Entities;

namespace Musterdepot.Persistence;

/// <summary>
/// Event-Log in der Tabelle <c>Events</c> (JSON über <see cref="JsonDefaults"/>). Beim Anhängen werden in derselben
/// Transaktion die Projektionen Accounts, Orders, Fills, Positions und CashEntries aktualisiert (für Export/Analyse;
/// Quelle der Wahrheit bleibt der Event-Log bzw. die Engine).
/// Events mit Sequence &gt; 0, deren (AccountId, Sequence) bereits gespeichert ist, werden übersprungen (idempotent).
/// </summary>
public sealed class SqliteEventStore(MusterdepotDatabase database) : IEventStore
{
    public async Task AppendAsync(IReadOnlyList<DomainEvent> events, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0) return;
        await database.WriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = database.CreateDbContext();
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

            var accountIds = events.Select(e => e.AccountId).Distinct().ToList();
            var maxSeq = new Dictionary<string, long>();
            foreach (var id in accountIds)
            {
                var max = await db.Events.Where(x => x.AccountId == id).Select(x => (long?)x.Sequence).MaxAsync(ct).ConfigureAwait(false);
                maxSeq[id] = max ?? 0;
            }

            var projector = new EventProjector(db);
            foreach (var e in events)
            {
                if (e.Sequence > 0)
                {
                    if (e.Sequence <= maxSeq[e.AccountId]) continue; // bereits gespeichert
                    maxSeq[e.AccountId] = e.Sequence;
                }
                db.Events.Add(new EventEntity
                {
                    AccountId = e.AccountId,
                    Sequence = e.Sequence,
                    Type = e.EventType,
                    SchemaVersion = e.SchemaVersion,
                    TimestampUtc = e.TimestampUtc,
                    EventId = string.IsNullOrEmpty(e.EventId) ? null : e.EventId,
                    Json = JsonDefaults.SerializeEvent(e),
                });
                await projector.ApplyAsync(e, ct).ConfigureAwait(false);
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            database.WriteLock.Release();
        }
    }

    public async Task<IReadOnlyList<DomainEvent>> ReadAllAsync(string? accountId = null, CancellationToken ct = default)
    {
        await using var db = database.CreateDbContext();
        var q = db.Events.AsNoTracking();
        if (accountId is not null) q = q.Where(x => x.AccountId == accountId);
        var rows = await q.OrderBy(x => x.Id).Select(x => x.Json).ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(JsonDefaults.DeserializeEvent).ToList();
    }
}

/// <summary>Aktualisiert die Projektionstabellen für ein Event (innerhalb eines Kontexts/Transaktion).</summary>
internal sealed class EventProjector(MusterdepotDbContext db)
{
    public async Task ApplyAsync(DomainEvent e, CancellationToken ct)
    {
        var account = await db.Accounts.FindAsync([e.AccountId], ct).ConfigureAwait(false);
        switch (e)
        {
            case AccountOpened o:
                if (account is null)
                {
                    account = new AccountEntity { AccountId = o.AccountId };
                    db.Accounts.Add(account);
                }
                account.Name = o.Name;
                account.BaseCurrency = o.BaseCurrency;
                account.CreatedUtc = o.TimestampUtc;
                account.InitialCash = o.InitialCash;
                account.Cash = o.InitialCash;
                account.RiskJson = Json(o.Risk);
                account.CryptoFeesJson = Json(o.CryptoFees);
                account.StockFeesJson = Json(o.StockFees);
                AddCash(e, "initial", o.InitialCash, "deposit", null, "Startkapital");
                break;
            case CashDeposited d:
                if (account is not null) account.Cash += d.Amount;
                AddCash(e, "deposit", d.Amount, "deposit", null, d.Note);
                break;
            case CashWithdrawn w:
                if (account is not null) account.Cash -= w.Amount;
                AddCash(e, "withdrawal", -w.Amount, "withdrawal", null, w.Note);
                break;
            case OrderSubmitted s:
                await UpsertOrderAsync(s.Order, ct).ConfigureAwait(false);
                break;
            case OrderAccepted a:
                await UpdateOrderAsync(a.OrderId, a.TimestampUtc, o =>
                {
                    if (o.Status == nameof(OrderStatus.PendingNew)) o.Status = nameof(OrderStatus.Working);
                }, ct).ConfigureAwait(false);
                break;
            case OrderRejected r:
            {
                var o = await db.Orders.FindAsync([r.OrderId], ct).ConfigureAwait(false);
                if (o is null)
                {
                    o = new OrderEntity
                    {
                        OrderId = r.OrderId, ClientOrderId = r.ClientOrderId, AccountId = r.AccountId, Symbol = r.Symbol,
                        Source = r.Source.ToString(), CreatedUtc = r.TimestampUtc,
                    };
                    db.Orders.Add(o);
                }
                o.Status = nameof(OrderStatus.Rejected);
                o.RejectReason = r.Reason;
                o.RiskRule = r.RiskRule;
                o.LastUpdateUtc = r.TimestampUtc;
                break;
            }
            case OrderModified m:
                await UpdateOrderAsync(m.OrderId, m.TimestampUtc, o =>
                {
                    if (m.NewQuantity is { } q) o.Quantity = q;
                    if (m.NewLimitPrice is { } l) o.LimitPrice = l;
                    if (m.NewStopPrice is { } s) o.StopPrice = s;
                }, ct).ConfigureAwait(false);
                break;
            case OrderTriggered t:
                await UpdateOrderAsync(t.OrderId, t.TimestampUtc, o =>
                {
                    o.IsTriggered = true;
                    if (o.Status is nameof(OrderStatus.Working) or nameof(OrderStatus.PendingNew)) o.Status = nameof(OrderStatus.Triggered);
                }, ct).ConfigureAwait(false);
                break;
            case TrailingStopAdjusted ts:
                await UpdateOrderAsync(ts.OrderId, ts.TimestampUtc, o =>
                {
                    o.StopPrice = ts.NewStopPrice;
                    o.TrailReferencePrice = ts.ReferencePrice;
                }, ct).ConfigureAwait(false);
                break;
            case OrderFilled f:
                await ApplyFillAsync(f, account, ct).ConfigureAwait(false);
                break;
            case OrderCancelled c:
                await UpdateOrderAsync(c.OrderId, c.TimestampUtc, o => { o.Status = nameof(OrderStatus.Cancelled); o.RejectReason ??= c.Reason; }, ct).ConfigureAwait(false);
                break;
            case OrderExpired x:
                await UpdateOrderAsync(x.OrderId, x.TimestampUtc, o => o.Status = nameof(OrderStatus.Expired), ct).ConfigureAwait(false);
                break;
            case KillSwitchEngaged:
                if (account is not null) account.KillSwitchEngaged = true;
                break;
            case KillSwitchReleased:
                if (account is not null) account.KillSwitchEngaged = false;
                break;
            case DailyLossLimitHit:
                if (account is not null) account.LastDailyLossLimitHitUtc = e.TimestampUtc;
                break;
            case RiskConfigChanged rc:
                if (account is not null) account.RiskJson = Json(rc.Risk);
                break;
            case FeeModelChanged fc:
                if (account is not null)
                {
                    if (fc.AssetClass == AssetClass.Crypto) account.CryptoFeesJson = Json(fc.Fees);
                    else account.StockFeesJson = Json(fc.Fees);
                }
                break;
            case MarketPriceMarked mp:
            {
                var p = await db.Positions.FindAsync([mp.AccountId, mp.Symbol], ct).ConfigureAwait(false);
                if (p is not null)
                {
                    p.LastPrice = mp.Price;
                    p.LastPriceUtc = mp.TimestampUtc;
                }
                break;
            }
        }
        if (account is not null)
        {
            if (e.Sequence > account.LastSequence) account.LastSequence = e.Sequence;
            account.UpdatedUtc = e.TimestampUtc;
        }
    }

    private async Task ApplyFillAsync(OrderFilled f, AccountEntity? account, CancellationToken ct)
    {
        var fill = f.Fill;
        if (await db.Fills.FindAsync([fill.FillId], ct).ConfigureAwait(false) is not null) return;
        db.Fills.Add(new FillEntity
        {
            FillId = fill.FillId, OrderId = fill.OrderId, AccountId = fill.AccountId, Symbol = fill.Symbol,
            Side = fill.Side.ToString(), Quantity = fill.Quantity, Price = fill.Price, Fee = fill.Fee,
            Liquidity = fill.Liquidity.ToString(), Quality = fill.Quality.ToString(), FxRate = fill.FxRate, FxSource = f.FxSource,
            TimestampUtc = fill.TimestampUtc, MarketDataAsOfUtc = fill.MarketDataAsOfUtc, ExecutedDuringGap = fill.ExecutedDuringGap,
            Source = fill.Source.ToString(), StrategyTag = fill.StrategyTag, RealizedPnl = fill.RealizedPnl,
        });

        await UpdateOrderAsync(fill.OrderId, f.TimestampUtc, o =>
        {
            var newFilled = o.FilledQuantity + fill.Quantity;
            if (newFilled != 0) o.AveragePrice = (o.AveragePrice * o.FilledQuantity + fill.Price * fill.Quantity) / newFilled;
            o.FilledQuantity = newFilled;
            o.FeesPaid += fill.Fee;
            o.Status = f.ResultingStatus.ToString();
        }, ct).ConfigureAwait(false);

        var gross = fill.Quantity * fill.Price * fill.FxRate;
        var sign = fill.Side == OrderSide.Buy ? -1m : 1m;
        AddCash(f, "trade", sign * gross, fill.Side == OrderSide.Buy ? "buy" : "sell", fill.FillId, $"{fill.Symbol} {fill.Quantity} @ {fill.Price}");
        if (fill.Fee != 0) AddCash(f, "fee", -fill.Fee, "fee", fill.FillId, null);
        if (account is not null)
        {
            account.Cash += sign * gross - fill.Fee;
            account.FeesPaid += fill.Fee;
            account.RealizedPnl += fill.RealizedPnl;
        }

        var pos = await db.Positions.FindAsync([fill.AccountId, fill.Symbol], ct).ConfigureAwait(false);
        if (pos is null)
        {
            pos = new PositionEntity { AccountId = fill.AccountId, Symbol = fill.Symbol };
            db.Positions.Add(pos);
        }
        var lots = JsonSerializer.Deserialize<List<Lot>>(pos.LotsJson, JsonDefaults.Options) ?? [];
        if (fill.Side == OrderSide.Buy)
        {
            var costPerUnit = fill.Quantity == 0 ? 0 : (gross + fill.Fee) / fill.Quantity;
            lots.Add(new Lot
            {
                LotId = fill.FillId, Symbol = fill.Symbol, Quantity = fill.Quantity, Price = fill.Price,
                CostPerUnitBase = costPerUnit, OpenedUtc = fill.TimestampUtc, OpeningFillId = fill.FillId,
                Source = fill.Source, StrategyTag = fill.StrategyTag,
            });
        }
        else
        {
            var remaining = fill.Quantity;
            for (var i = 0; i < lots.Count && remaining > 0;)
            {
                var lot = lots[i];
                if (lot.Quantity <= remaining)
                {
                    remaining -= lot.Quantity;
                    lots.RemoveAt(i);
                }
                else
                {
                    lots[i] = lot with { Quantity = lot.Quantity - remaining };
                    remaining = 0;
                }
            }
        }
        pos.RealizedPnl += fill.RealizedPnl;
        pos.Quantity = lots.Sum(l => l.Quantity);
        pos.CostBasis = lots.Sum(l => l.Quantity * l.CostPerUnitBase);
        pos.AverageCost = pos.Quantity == 0 ? 0 : lots.Sum(l => l.Quantity * l.Price) / pos.Quantity;
        pos.LastPrice = fill.Price;
        pos.LastPriceUtc = fill.TimestampUtc;
        pos.LotsJson = JsonSerializer.Serialize(lots, JsonDefaults.Options);
        pos.UpdatedUtc = fill.TimestampUtc;
    }

    private async Task UpsertOrderAsync(Order order, CancellationToken ct)
    {
        var o = await db.Orders.FindAsync([order.OrderId], ct).ConfigureAwait(false);
        if (o is null)
        {
            o = new OrderEntity { OrderId = order.OrderId };
            db.Orders.Add(o);
        }
        o.ClientOrderId = order.ClientOrderId;
        o.AccountId = order.AccountId;
        o.Symbol = order.Symbol;
        o.Side = order.Side.ToString();
        o.Type = order.Type.ToString();
        o.TimeInForce = order.TimeInForce.ToString();
        o.Quantity = order.Quantity;
        o.LimitPrice = order.LimitPrice;
        o.StopPrice = order.StopPrice;
        o.TrailAmount = order.TrailAmount;
        o.TrailPercent = order.TrailPercent;
        o.TrailReferencePrice = order.TrailReferencePrice;
        o.ExpiresUtc = order.ExpiresUtc;
        o.Source = order.Source.ToString();
        o.ClientName = order.ClientName;
        o.StrategyTag = order.StrategyTag;
        o.OcoGroupId = order.OcoGroupId;
        o.ParentOrderId = order.ParentOrderId;
        o.BracketRole = order.BracketRole.ToString();
        o.CreatedUtc = order.CreatedUtc;
        o.Status = order.Status.ToString();
        o.FilledQuantity = order.FilledQuantity;
        o.AveragePrice = order.AveragePrice;
        o.FeesPaid = order.FeesPaid;
        o.IsTriggered = order.IsTriggered;
        o.RejectReason = order.RejectReason;
        o.LastUpdateUtc = order.LastUpdateUtc ?? order.CreatedUtc;
    }

    private async Task UpdateOrderAsync(string orderId, DateTimeOffset ts, Action<OrderEntity> update, CancellationToken ct)
    {
        var o = await db.Orders.FindAsync([orderId], ct).ConfigureAwait(false);
        if (o is null) return;
        update(o);
        o.LastUpdateUtc = ts;
    }

    private void AddCash(DomainEvent e, string suffix, decimal amount, string kind, string? reference, string? note)
    {
        var id = $"{e.AccountId}:{(e.Sequence > 0 ? e.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) : (string.IsNullOrEmpty(e.EventId) ? Guid.NewGuid().ToString("N") : e.EventId))}:{suffix}";
        if (db.CashEntries.Local.Any(c => c.EntryId == id)) return;
        db.CashEntries.Add(new CashEntryEntity
        {
            EntryId = id, AccountId = e.AccountId, TimestampUtc = e.TimestampUtc, Amount = amount, Kind = kind,
            Reference = reference, Note = note, EventSequence = e.Sequence,
        });
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonDefaults.Options);
}
