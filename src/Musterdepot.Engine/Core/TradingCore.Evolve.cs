using System.Collections.Immutable;
using Musterdepot.Core.Domain;
using Musterdepot.Core.Events;
using Musterdepot.Engine.Calendar;
using Musterdepot.Engine.State;

namespace Musterdepot.Engine.Core;

/// <summary>
/// Kern der Engine: zwei reine Funktionen. <see cref="Evolve"/> (Zustand + Event → neuer Zustand) und
/// <c>Decide</c> (Befehl/Marktdaten + Zustand + Kontext → Events). Uhr, IDs und externe Kurse kommen über den Kontext.
/// </summary>
public static partial class TradingCore
{
    /// <summary>Wendet ein Event an. Deterministisch; hängt nur von Zustand und Event ab.</summary>
    public static AccountState Evolve(AccountState? state, DomainEvent e)
    {
        if (e is AccountOpened opened) return Open(opened);
        if (state is null) throw new InvalidOperationException($"Event {e.EventType} für unbekanntes Konto '{e.AccountId}'.");

        var s = RollTradingDay(state, e.TimestampUtc);
        s = e switch
        {
            CashDeposited d => AddEquityPoint(s with
            {
                Cash = s.Cash + d.Amount,
                TotalDeposits = s.TotalDeposits + d.Amount,
                CashEntries = s.CashEntries.Add(new CashEntry { EntryId = EntryId(e, "deposit"), AccountId = s.AccountId, TimestampUtc = e.TimestampUtc, Amount = d.Amount, Kind = "deposit", Note = d.Note }),
            }, e.TimestampUtc),
            CashWithdrawn w => AddEquityPoint(s with
            {
                Cash = s.Cash - w.Amount,
                TotalWithdrawals = s.TotalWithdrawals + w.Amount,
                CashEntries = s.CashEntries.Add(new CashEntry { EntryId = EntryId(e, "withdrawal"), AccountId = s.AccountId, TimestampUtc = e.TimestampUtc, Amount = -w.Amount, Kind = "withdrawal", Note = w.Note }),
            }, e.TimestampUtc),
            OrderSubmitted os => ApplySubmitted(s, os),
            OrderAccepted oa => UpdateOrder(s, oa.OrderId, o => o.Status == OrderStatus.PendingNew ? o with { Status = OrderStatus.Working, LastUpdateUtc = e.TimestampUtc } : o),
            OrderRejected or => s with { Rejections = s.Rejections.Add(or) },
            OrderModified om => UpdateOrder(s, om.OrderId, o => o with
            {
                Quantity = om.NewQuantity ?? o.Quantity,
                LimitPrice = om.NewLimitPrice ?? o.LimitPrice,
                StopPrice = om.NewStopPrice ?? o.StopPrice,
                Status = om.NewQuantity is { } nq && nq <= o.FilledQuantity && o.FilledQuantity > 0 ? OrderStatus.Filled : o.Status,
                LastUpdateUtc = e.TimestampUtc,
            }),
            OrderTriggered ot => UpdateOrder(s, ot.OrderId, o => o with
            {
                IsTriggered = true,
                Status = o.Status is OrderStatus.PendingNew or OrderStatus.Working ? OrderStatus.Triggered : o.Status,
                LastUpdateUtc = e.TimestampUtc,
            }),
            TrailingStopAdjusted ta => UpdateOrder(s, ta.OrderId, o => o with { StopPrice = ta.NewStopPrice, TrailReferencePrice = ta.ReferencePrice, LastUpdateUtc = e.TimestampUtc }),
            OrderFilled f => ApplyFill(s, f),
            OrderCancelled oc => UpdateOrder(s, oc.OrderId, o => o.IsOpen ? o with { Status = OrderStatus.Cancelled, RejectReason = oc.Reason, LastUpdateUtc = e.TimestampUtc } : o),
            OrderExpired ox => UpdateOrder(s, ox.OrderId, o => o.IsOpen ? o with { Status = OrderStatus.Expired, LastUpdateUtc = e.TimestampUtc } : o),
            KillSwitchEngaged k => s with { KillSwitchEngaged = true, KillSwitchReason = k.Reason },
            KillSwitchReleased => s with { KillSwitchEngaged = false, KillSwitchReason = null },
            DailyLossLimitHit => s with { DailyLossHitDay = BerlinTime.TradingDay(e.TimestampUtc) },
            RiskConfigChanged rc => s with { Risk = rc.Risk },
            FeeModelChanged fm => fm.AssetClass == AssetClass.Crypto ? s with { CryptoFees = fm.Fees } : s with { StockFees = fm.Fees },
            MarketPriceMarked m => ApplyMark(s, m),
            _ => s,
        };
        return e.Sequence > s.LastSequence ? s with { LastSequence = e.Sequence } : s;
    }

    /// <summary>Spielt Events eines Kontos ab (LoadFromEvents).</summary>
    public static AccountState? Replay(IEnumerable<DomainEvent> events)
    {
        AccountState? s = null;
        foreach (var e in events) s = Evolve(s, e);
        return s;
    }

    private static string EntryId(DomainEvent e, string kind)
        => string.IsNullOrEmpty(e.EventId) ? $"cash_{e.AccountId}_{e.Sequence}_{kind}" : $"{e.EventId}:{kind}";

    private static AccountState Open(AccountOpened o)
    {
        var s = new AccountState
        {
            Account = new Account { AccountId = o.AccountId, Name = o.Name, BaseCurrency = o.BaseCurrency, CreatedUtc = o.TimestampUtc, InitialCash = o.InitialCash },
            Cash = o.InitialCash,
            InitialCash = o.InitialCash,
            Risk = o.Risk,
            CryptoFees = o.CryptoFees,
            StockFees = o.StockFees,
            TradingDay = BerlinTime.TradingDay(o.TimestampUtc),
            DayStartEquity = o.InitialCash,
            LastSequence = o.Sequence,
        };
        s = s with
        {
            CashEntries = [new CashEntry { EntryId = EntryId(o, "initial"), AccountId = o.AccountId, TimestampUtc = o.TimestampUtc, Amount = o.InitialCash, Kind = "initial", Note = "Anfangskapital" }],
        };
        return AddEquityPoint(s, o.TimestampUtc);
    }

    private static AccountState RollTradingDay(AccountState s, DateTimeOffset ts)
    {
        var day = BerlinTime.TradingDay(ts);
        return day == s.TradingDay ? s : s with { TradingDay = day, DayStartEquity = s.MarkedEquity() };
    }

    private static AccountState AddEquityPoint(AccountState s, DateTimeOffset ts)
    {
        var pv = s.MarkedPositionsValue();
        var snap = new EquitySnapshot(ts, s.Cash, pv, s.Cash + pv);
        var curve = s.EquityCurve.Count > 0 && s.EquityCurve[^1].TimestampUtc == ts
            ? s.EquityCurve.SetItem(s.EquityCurve.Count - 1, snap)
            : s.EquityCurve.Add(snap);
        return s with { EquityCurve = curve };
    }

    private static AccountState UpdateOrder(AccountState s, string orderId, Func<Order, Order> update)
        => s.Orders.TryGetValue(orderId, out var o) ? SetOrder(s, update(o)) : s;

    private static AccountState SetOrder(AccountState s, Order o)
    {
        s = s with { Orders = s.Orders.SetItem(o.OrderId, o) };
        return !o.IsOpen && s.OpenOrderIds.Contains(o.OrderId) ? s with { OpenOrderIds = s.OpenOrderIds.Remove(o.OrderId) } : s;
    }

    private static AccountState ApplySubmitted(AccountState s, OrderSubmitted os)
    {
        var o = os.Order;
        if (s.Orders.ContainsKey(o.OrderId)) return s;
        var recent = s.RecentSubmissions.RemoveAll(x => x.TimestampUtc <= os.TimestampUtc - TimeSpan.FromMinutes(1));
        if (o.ParentOrderId is null) recent = recent.Add(new SubmissionStamp(os.TimestampUtc, o.Source));
        return s with
        {
            Orders = s.Orders.Add(o.OrderId, o),
            OrderSequence = s.OrderSequence.Add(o.OrderId),
            OpenOrderIds = o.IsOpen ? s.OpenOrderIds.Add(o.OrderId) : s.OpenOrderIds,
            ClientOrderIndex = s.ClientOrderIndex.SetItem(o.ClientOrderId, o.OrderId),
            RecentSubmissions = recent,
        };
    }

    private static AccountState ApplyMark(AccountState s, MarketPriceMarked m)
    {
        var fx = s.Marks.TryGetValue(m.Symbol, out var old) ? old.FxRate
            : s.Lots.TryGetValue(m.Symbol, out var lots) && lots.Count > 0 ? lots[^1].FxRate : 1m;
        s = s with { Marks = s.Marks.SetItem(m.Symbol, new MarkState(m.Price, fx, m.TimestampUtc)), LastMarkUtc = m.TimestampUtc };
        return AddEquityPoint(s, m.TimestampUtc);
    }

    private static AccountState ApplyFill(AccountState s, OrderFilled ev)
    {
        var f = ev.Fill;
        var gross = Ledger.Gross(f.Quantity, f.Price, f.FxRate);
        var ts = ev.TimestampUtc;

        if (s.Orders.TryGetValue(f.OrderId, out var order))
        {
            var newFilled = order.FilledQuantity + f.Quantity;
            var avg = newFilled == 0 ? 0 : Math.Round((order.AveragePrice * order.FilledQuantity + f.Price * f.Quantity) / newFilled, 10, MidpointRounding.AwayFromZero);
            s = SetOrder(s, order with
            {
                FilledQuantity = newFilled,
                AveragePrice = Money.Normalize(avg),
                FeesPaid = order.FeesPaid + f.Fee,
                Status = ev.ResultingStatus,
                LastUpdateUtc = ts,
            });
        }
        s = s with
        {
            OrderFilledNotional = s.OrderFilledNotional.SetItem(f.OrderId, (s.OrderFilledNotional.TryGetValue(f.OrderId, out var n) ? n : 0m) + gross),
            FeesPaid = s.FeesPaid + f.Fee,
            Fills = s.Fills.Add(f),
        };

        var entries = s.CashEntries.ToBuilder();
        var lots = s.Lots.TryGetValue(f.Symbol, out var l) ? l : [];
        if (f.Side == OrderSide.Buy)
        {
            s = s with { Cash = s.Cash - gross - f.Fee };
            entries.Add(new CashEntry { EntryId = $"{f.FillId}:buy", AccountId = s.AccountId, TimestampUtc = ts, Amount = -gross, Kind = "buy", Reference = f.FillId });
            lots = lots.Add(new LotState
            {
                LotId = $"lot_{f.FillId}",
                Symbol = f.Symbol,
                OrderId = f.OrderId,
                OpeningFillId = f.FillId,
                Quantity = f.Quantity,
                OriginalQuantity = f.Quantity,
                Price = f.Price,
                FxRate = f.FxRate,
                Cost = gross,
                Fee = f.Fee,
                OpenedUtc = f.TimestampUtc,
                Source = f.Source,
                StrategyTag = f.StrategyTag,
            });
        }
        else
        {
            var (remaining, slices) = Ledger.Consume(lots, f.Quantity);
            lots = remaining;
            var realizedNet = Ledger.RealizedNet(gross, f.Fee, slices);
            var realizedGross = gross - slices.Sum(x => x.Cost);
            s = s with
            {
                Cash = s.Cash + gross - f.Fee,
                RealizedPnl = s.RealizedPnl + realizedNet,
                RealizedPnlGross = s.RealizedPnlGross + realizedGross,
                RealizedBySymbol = s.RealizedBySymbol.SetItem(f.Symbol, (s.RealizedBySymbol.TryGetValue(f.Symbol, out var r) ? r : 0m) + realizedNet),
            };
            entries.Add(new CashEntry { EntryId = $"{f.FillId}:sell", AccountId = s.AccountId, TimestampUtc = ts, Amount = gross, Kind = "sell", Reference = f.FillId });

            if (slices.Count > 0)
            {
                var qtys = slices.Select(x => x.Quantity).ToList();
                var grossAlloc = Ledger.Allocate(gross, qtys);
                var feeAlloc = Ledger.Allocate(f.Fee, qtys);
                var rts = s.RoundTrips.ToBuilder();
                for (int i = 0; i < slices.Count; i++)
                {
                    var sl = slices[i];
                    var key = $"{f.OrderId}|{sl.Lot.OrderId}";
                    var rt = rts.TryGetValue(key, out var existing) ? existing : new RoundTrip
                    {
                        Key = key,
                        Symbol = f.Symbol,
                        OpenOrderId = sl.Lot.OrderId,
                        CloseOrderId = f.OrderId,
                        OpenedUtc = sl.Lot.OpenedUtc,
                        ClosedUtc = f.TimestampUtc,
                        Source = sl.Lot.Source,
                        StrategyTag = sl.Lot.StrategyTag,
                    };
                    rts[key] = rt with
                    {
                        Quantity = rt.Quantity + sl.Quantity,
                        EntryCost = rt.EntryCost + sl.Cost,
                        ExitProceeds = rt.ExitProceeds + grossAlloc[i],
                        Fees = rt.Fees + sl.Fee + feeAlloc[i],
                        OpenedUtc = sl.Lot.OpenedUtc < rt.OpenedUtc ? sl.Lot.OpenedUtc : rt.OpenedUtc,
                        ClosedUtc = f.TimestampUtc,
                    };
                }
                s = s with { RoundTrips = rts.ToImmutable() };
            }
        }
        if (f.Fee != 0)
            entries.Add(new CashEntry { EntryId = $"{f.FillId}:fee", AccountId = s.AccountId, TimestampUtc = ts, Amount = -f.Fee, Kind = "fee", Reference = f.FillId });

        s = s with
        {
            CashEntries = entries.ToImmutable(),
            Lots = lots.Count > 0 ? s.Lots.SetItem(f.Symbol, lots) : s.Lots.Remove(f.Symbol),
            Marks = s.Marks.SetItem(f.Symbol, new MarkState(f.Price, f.FxRate, f.MarketDataAsOfUtc == default ? ts : f.MarketDataAsOfUtc)),
        };
        return AddEquityPoint(s, ts);
    }
}
