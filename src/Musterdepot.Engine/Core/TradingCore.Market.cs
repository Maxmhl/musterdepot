using Musterdepot.Core.Domain;
using Musterdepot.Core.Events;
using Musterdepot.Core.MarketData;
using Musterdepot.Engine.Fees;
using Musterdepot.Engine.Market;
using Musterdepot.Engine.State;

namespace Musterdepot.Engine.Core;

public static partial class TradingCore
{
    /// <summary>
    /// Decide für Marktdaten. <paramref name="market"/> enthält das Ereignis bereits (<see cref="MarketState.Apply"/>).
    /// Ablauf: Zeitablauf → Orders gegen das Ereignis prüfen (bzw. Lücken-Kerzen) → Bewertung (max. 1×/Minute) → Tagesverlust.
    /// </summary>
    public static IReadOnlyList<DomainEvent> Decide(AccountState state, MarketEvent marketEvent, MarketState market, EngineContext ctx)
    {
        var s = new DecisionSession(state, market, ctx);
        ExpireByTime(s);
        switch (marketEvent)
        {
            case GapDetected gap:
                ProcessGap(s, gap);
                break;
            case TradeTick or QuoteTick or OrderBookUpdate:
                ProcessOrders(s, marketEvent);
                break;
        }
        MaybeMark(s);
        CheckDailyLoss(s);
        return s.Events;
    }

    /// <summary>Decide für Zeitfortschritt: Ablauf DAY/GTD, Abbruch wartender Market-Orders ohne frische Daten.</summary>
    public static IReadOnlyList<DomainEvent> DecideClockTick(AccountState state, MarketState market, EngineContext ctx)
    {
        var s = new DecisionSession(state, market, ctx);
        ExpireByTime(s);
        return s.Events;
    }

    // ------------------------------------------------------------------ Zeit

    private static void ExpireByTime(DecisionSession s)
    {
        foreach (var o in s.State.OpenOrders.ToList())
        {
            var cur = s.Reload(o);
            if (!cur.IsOpen) continue;
            var sym = s.Market.Get(cur.Symbol);
            var delayAllowance = sym is { IsDelayed: true } ? TimeSpan.FromMinutes(sym.DelayedMinutes) + s.Ctx.Options.DelayedDataTolerance : TimeSpan.Zero;
            if (cur.ExpiresUtc is { } exp && s.Now >= exp + delayAllowance)
            {
                ExpireOrder(s, cur);
                continue;
            }
            // Wartende Market-Order ohne passende Daten: abbrechen, sobald auch verzögerte Daten (Verzögerung + Dateiintervall,
            // doppelte Toleranz) längst da sein müssten.
            var pendingLimit = AllowedQuoteAge(sym, s.State.Risk, s.Ctx) + (sym is { IsDelayed: true } ? s.Ctx.Options.DelayedDataTolerance : TimeSpan.Zero);
            if (cur.Type == OrderType.Market && s.Now - cur.EligibleFromUtc > pendingLimit)
                CancelOrder(s, cur, "Keine frischen Marktdaten für die Market-Order", cur.Source);
        }
    }

    // ------------------------------------------------------------------ Echtzeit / verzögert

    private static void ProcessOrders(DecisionSession s, MarketEvent evt)
    {
        var sym = s.Market.Get(evt.Symbol);
        var delayed = (sym?.IsDelayed ?? false) || evt.DelayedMinutes > 0 || evt.Quality == DataQuality.Delayed;
        s.MakerVolumeUsed = 0;
        foreach (var id in s.State.OrderSequence.ToList())
        {
            var o = s.State.Orders[id];
            if (!o.IsOpen || o.Symbol != evt.Symbol || !IsActive(s.State, o)) continue;
            // Latenz bzw. verzögerte Daten: nur Daten ab EligibleFromUtc (Eingang + Latenz) zählen.
            if (evt.AsOfUtc < o.EligibleFromUtc) continue;
            if (o.ExpiresUtc is { } exp && evt.AsOfUtc >= exp)
            {
                ExpireOrder(s, o);
                continue;
            }
            // Verzögerte Aktien: Ausführung ausschließlich gegen Trades.
            if (delayed && evt is not TradeTick) continue;
            var inst = s.Ctx.Instruments.Resolve(o.Symbol);
            if (inst is null) continue;
            ProcessOrder(s, o, evt, inst, delayed);
        }
    }

    private static void ProcessOrder(DecisionSession s, Order o, MarketEvent evt, Instrument inst, bool delayed)
    {
        var first = o.Status == OrderStatus.PendingNew;
        if (first)
        {
            s.Emit(new OrderAccepted { AccountId = s.AccountId, TimestampUtc = s.Now, OrderId = o.OrderId });
            o = s.Reload(o);
        }
        var immediate = o.TimeInForce is TimeInForce.Ioc or TimeInForce.Fok;
        var trade = evt as TradeTick;

        switch (o.Type)
        {
            case OrderType.Market:
                TakerAttempt(s, o, evt, inst, delayed, bound: null, allOrNothing: o.TimeInForce == TimeInForce.Fok && first);
                if (immediate) CancelRest(s, o, "IOC: Rest storniert");
                break;

            case OrderType.Limit:
                if (first)
                {
                    // Erste Prüfung: sofort ausführbarer Teil nimmt Liquidität (Taker), Rest ruht (Maker).
                    TakerAttempt(s, o, evt, inst, delayed, bound: o.LimitPrice, allOrNothing: o.TimeInForce == TimeInForce.Fok);
                    if (immediate) CancelRest(s, o, o.TimeInForce == TimeInForce.Fok ? "FOK: nicht vollständig ausführbar" : "IOC: Rest storniert");
                }
                else if (trade is not null) MakerAttempt(s, o, trade, inst, delayed);
                break;

            case OrderType.Stop or OrderType.StopLimit or OrderType.TrailingStop when !o.IsTriggered:
                if (trade is null) break;
                if (o.Type == OrderType.TrailingStop)
                {
                    AdjustTrailing(s, o, trade.Price, inst);
                    o = s.Reload(o);
                }
                if (!IsStopHit(o, trade.Price)) break;
                s.Emit(new OrderTriggered { AccountId = s.AccountId, TimestampUtc = s.Now, OrderId = o.OrderId, TriggerPrice = trade.Price });
                o = s.Reload(o);
                // Ausführung zum ersten verfügbaren Kurs (bei Kurssprung schlechter als der Stop-Preis).
                TakerAttempt(s, o, evt, inst, delayed, bound: o.Type == OrderType.StopLimit ? o.LimitPrice : null, allOrNothing: o.TimeInForce == TimeInForce.Fok);
                if (immediate) CancelRest(s, o, "IOC: Rest nach Auslösung storniert");
                break;

            case OrderType.StopLimit:
                if (trade is not null) MakerAttempt(s, o, trade, inst, delayed);
                break;

            default: // ausgelöste Stop/Trailing mit Restmenge
                TakerAttempt(s, o, evt, inst, delayed, bound: null, allOrNothing: false);
                break;
        }
    }

    private static void CancelRest(DecisionSession s, Order o, string reason)
    {
        var cur = s.Reload(o);
        if (cur.IsOpen) CancelOrder(s, cur, reason, cur.Source);
    }

    private static bool IsStopHit(Order o, decimal price)
        => o.StopPrice is { } stop && (o.Side == OrderSide.Buy ? price >= stop : price <= stop);

    private static void AdjustTrailing(DecisionSession s, Order o, decimal price, Instrument inst)
    {
        var reference = o.TrailReferencePrice ?? price;
        var improves = o.Side == OrderSide.Sell ? price > reference : price < reference;
        if (!improves) return;
        var dist = o.TrailAmount ?? price * (o.TrailPercent ?? 0) / 100m;
        var newStop = o.Side == OrderSide.Sell
            ? Money.RoundToTick(price - dist, inst.TickSize, up: false)
            : Money.RoundToTick(price + dist, inst.TickSize, up: true);
        var better = o.StopPrice is not { } old || (o.Side == OrderSide.Sell ? newStop > old : newStop < old);
        if (better && newStop > 0)
            s.Emit(new TrailingStopAdjusted { AccountId = s.AccountId, TimestampUtc = s.Now, OrderId = o.OrderId, NewStopPrice = Money.Normalize(newStop), ReferencePrice = price });
    }

    private record struct Candidate(decimal Quantity, decimal Price, FillQuality Quality, DateTimeOffset AsOf, decimal? BookLevel);

    /// <summary>
    /// Taker-Ausführung: verzögert → letzter Trade ± halber angenommener Spread (Synthetic); Echtzeit mit Orderbuch →
    /// Stufe für Stufe durch das zuletzt bekannte Buch (OrderBook); sonst Ask/Bid bzw. letzter Trade (Trade).
    /// </summary>
    private static decimal TakerAttempt(DecisionSession s, Order o, MarketEvent evt, Instrument inst, bool delayed, decimal? bound, bool allOrNothing)
    {
        o = s.Reload(o);
        var remaining = o.RemainingQuantity;
        if (remaining <= 0 || !o.IsOpen) return 0;
        var candidates = new List<Candidate>();
        var sym = s.Market.Get(o.Symbol);
        var maxAge = s.State.Risk.MaxQuoteAge;

        if (delayed)
        {
            if (evt is not TradeTick t) return 0;
            var half = s.Ctx.Execution.SyntheticSpreadPercent / 200m;
            var price = o.Side == OrderSide.Buy
                ? Money.RoundToTick(t.Price * (1 + half), inst.TickSize, up: true)
                : Money.RoundToTick(t.Price * (1 - half), inst.TickSize, up: false);
            if (bound is { } b && (o.Side == OrderSide.Buy ? price > b : price < b)) return 0;
            candidates.Add(new Candidate(remaining, Money.Normalize(price), FillQuality.Synthetic, t.AsOfUtc, null));
        }
        else if (sym is not null)
        {
            var book = o.Side == OrderSide.Buy ? sym.BookAsks : sym.BookBids;
            var bookSide = o.Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
            if (book.Count > 0 && sym.BookAsOfUtc is { } bookAt && bookAt >= evt.AsOfUtc - maxAge)
            {
                var left = remaining;
                var levels = 0;
                foreach (var (price, qty) in book)
                {
                    if (left <= 0 || levels++ >= s.Ctx.Options.MaxBookLevelsPerSweep) break;
                    if (bound is { } b && (o.Side == OrderSide.Buy ? price > b : price < b)) break;
                    var used = s.BookConsumed.TryGetValue((o.Symbol, bookSide, price), out var u) ? u : 0m;
                    var avail = Money.FloorToStep(qty - used, inst.QuantityStep);
                    if (avail <= 0) continue;
                    var take = Math.Min(left, avail);
                    candidates.Add(new Candidate(take, price, FillQuality.OrderBook, bookAt, price));
                    left -= take;
                }
            }
            else
            {
                var quoteFresh = sym.QuoteAsOfUtc is { } qa && qa >= evt.AsOfUtc - maxAge;
                decimal? price = null;
                DateTimeOffset asOf = evt.AsOfUtc;
                if (quoteFresh && (o.Side == OrderSide.Buy ? sym.Ask : sym.Bid) is { } q) { price = q; asOf = sym.QuoteAsOfUtc!.Value; }
                else if (sym.LastTradePrice is { } lt && sym.LastTradeAsOfUtc is { } la && la >= evt.AsOfUtc - maxAge) { price = lt; asOf = la; }
                if (price is { } p && (bound is not { } b || (o.Side == OrderSide.Buy ? p <= b : p >= b)))
                    candidates.Add(new Candidate(remaining, p, FillQuality.Trade, asOf, null));
            }
        }
        if (candidates.Count == 0) return 0;
        if (allOrNothing && !CanFillCompletely(s, o, inst, candidates, Liquidity.Taker)) return 0;

        decimal filled = 0;
        foreach (var c in candidates)
        {
            var q = FillAt(s, o, inst, c.Quantity, c.Price, Liquidity.Taker, c.Quality, c.AsOf, gap: false);
            if (q > 0 && c.BookLevel is { } level)
            {
                var key = (o.Symbol, o.Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy, level);
                s.BookConsumed[key] = (s.BookConsumed.TryGetValue(key, out var u) ? u : 0m) + q;
            }
            filled += q;
            if (q < c.Quantity || !s.Reload(o).IsOpen) break;
        }
        return filled;
    }

    /// <summary>
    /// Ruhende Limits (Maker): Fill nur, wenn ein Trade echt durch das Limit geht (Kauf: Trade &lt; Limit, Verkauf: Trade &gt; Limit).
    /// Trade genau auf dem Limit füllt nur, wenn die Gegenseite ihn ausgelöst hat (AggressorSide bekannt und entgegengesetzt).
    /// Menge gedeckelt auf <c>LimitFillVolumeShare</c> des Trade-Volumens (über alle Orders des Kontos), Preis = Limit.
    /// </summary>
    private static void MakerAttempt(DecisionSession s, Order o, TradeTick t, Instrument inst, bool delayed)
    {
        o = s.Reload(o);
        if (o.LimitPrice is not { } limit || o.RemainingQuantity <= 0) return;
        var through = o.Side == OrderSide.Buy
            ? t.Price < limit || (t.Price == limit && t.AggressorSide == OrderSide.Sell)
            : t.Price > limit || (t.Price == limit && t.AggressorSide == OrderSide.Buy);
        if (!through) return;
        var cap = Money.FloorToStep(t.Quantity * s.Ctx.Execution.LimitFillVolumeShare - s.MakerVolumeUsed, inst.QuantityStep);
        var q = Math.Min(o.RemainingQuantity, cap);
        if (q <= 0) return;
        var filled = FillAt(s, o, inst, q, limit, Liquidity.Maker, delayed ? FillQuality.Synthetic : FillQuality.Trade, t.AsOfUtc, gap: false);
        s.MakerVolumeUsed += filled;
    }

    // ------------------------------------------------------------------ Lücke (nachgeladene Kerzen)

    /// <summary>
    /// Offene Orders werden Kerze für Kerze geprüft (nur Kerzen, die nach EligibleFromUtc beginnen). Konservativer OHLC-Pfad:
    /// steigende Kerze O→L→H→C, fallende O→H→L→C. Stops: Auslösung, wenn der Pfad den Stop erreicht; Fill zum Eröffnungskurs,
    /// falls schon dieser jenseits des Stops liegt (Kurssprung), sonst zum Stop-Preis. Limits: nur wenn Hoch/Tief echt durch das
    /// Limit geht, Fill zum Limit, gedeckelt auf den Volumenanteil der Kerze. Alle Fills: FillQuality.Gap, ExecutedDuringGap.
    /// Kinder einer Bracket werden frühestens in der Kerze nach dem Einstiegs-Fill geprüft.
    /// </summary>
    private static void ProcessGap(DecisionSession s, GapDetected gap)
    {
        foreach (var c in gap.BackfilledCandles.Where(c => c.Symbol == gap.Symbol || string.IsNullOrEmpty(c.Symbol)).OrderBy(c => c.OpenTimeUtc))
        {
            var start = c.OpenTimeUtc;
            var batch = s.State.OrderSequence.Select(id => s.State.Orders[id])
                .Where(o => o.IsOpen && o.Symbol == gap.Symbol && IsActive(s.State, o) && o.EligibleFromUtc <= start)
                .Select(o => o.OrderId).ToList();
            var volumeUsed = 0m;
            foreach (var id in batch)
            {
                var o = s.State.Orders[id];
                if (!o.IsOpen) continue;
                if (o.ExpiresUtc is { } exp && start >= exp) { ExpireOrder(s, o); continue; }
                var inst = s.Ctx.Instruments.Resolve(o.Symbol);
                if (inst is null) continue;
                volumeUsed += ProcessOrderOnCandle(s, o, c, inst, volumeUsed);
            }
        }
    }

    private static decimal ProcessOrderOnCandle(DecisionSession s, Order o, Candle c, Instrument inst, decimal volumeUsed)
    {
        var first = o.Status == OrderStatus.PendingNew;
        if (first)
        {
            s.Emit(new OrderAccepted { AccountId = s.AccountId, TimestampUtc = s.Now, OrderId = o.OrderId });
            o = s.Reload(o);
        }
        var immediate = o.TimeInForce is TimeInForce.Ioc or TimeInForce.Fok;
        var path = c.Close >= c.Open ? new[] { c.Open, c.Low, c.High, c.Close } : new[] { c.Open, c.High, c.Low, c.Close };
        var asOf = c.OpenTimeUtc;
        decimal makerFilled = 0;

        bool Marketable(decimal price, decimal limit) => o.Side == OrderSide.Buy ? price <= limit : price >= limit;

        decimal Taker(decimal price) => GapFill(s, o, inst, price, Liquidity.Taker, asOf, o.TimeInForce == TimeInForce.Fok);

        decimal Maker(decimal limit)
        {
            var through = o.Side == OrderSide.Buy ? c.Low < limit : c.High > limit;
            if (!through) return 0;
            var cap = Money.FloorToStep(c.Volume * s.Ctx.Execution.LimitFillVolumeShare - volumeUsed, inst.QuantityStep);
            var q = Math.Min(s.Reload(o).RemainingQuantity, cap);
            if (q <= 0) return 0;
            var f = FillAt(s, o, inst, q, limit, Liquidity.Maker, FillQuality.Gap, asOf, gap: true);
            makerFilled += f;
            return f;
        }

        switch (o.Type)
        {
            case OrderType.Market:
                Taker(c.Open);
                if (immediate) CancelRest(s, o, "IOC: Rest storniert");
                break;
            case OrderType.Limit:
                if (first && Marketable(c.Open, o.LimitPrice!.Value)) Taker(c.Open);
                else if (!immediate) Maker(o.LimitPrice!.Value);
                if (immediate) CancelRest(s, o, "IOC: Rest storniert");
                break;
            case OrderType.StopLimit when o.IsTriggered:
                Maker(o.LimitPrice!.Value);
                break;
            case OrderType.Stop or OrderType.TrailingStop when o.IsTriggered:
                Taker(c.Open);
                break;
            default: // Stop, StopLimit, TrailingStop – noch nicht ausgelöst
                for (int i = 0; i < path.Length; i++)
                {
                    var p = path[i];
                    o = s.Reload(o);
                    if (o.Type == OrderType.TrailingStop)
                    {
                        // Erst auslösen prüfen (Pfad kommt aus der Vorgeschichte), dann nachziehen.
                        if (!IsStopHit(o, p)) { AdjustTrailing(s, o, p, inst); continue; }
                    }
                    else if (!IsStopHit(o, p)) continue;
                    var fillPrice = i == 0 ? p : o.StopPrice!.Value;
                    s.Emit(new OrderTriggered { AccountId = s.AccountId, TimestampUtc = s.Now, OrderId = o.OrderId, TriggerPrice = fillPrice });
                    o = s.Reload(o);
                    if (o.Type == OrderType.StopLimit)
                    {
                        if (Marketable(fillPrice, o.LimitPrice!.Value)) Taker(fillPrice);
                    }
                    else Taker(fillPrice);
                    if (immediate) CancelRest(s, o, "IOC: Rest nach Auslösung storniert");
                    break;
                }
                break;
        }
        return makerFilled;
    }

    private static decimal GapFill(DecisionSession s, Order o, Instrument inst, decimal price, Liquidity liq, DateTimeOffset asOf, bool allOrNothing)
    {
        o = s.Reload(o);
        if (!o.IsOpen || o.RemainingQuantity <= 0) return 0;
        var cand = new List<Candidate> { new(o.RemainingQuantity, price, FillQuality.Gap, asOf, null) };
        if (allOrNothing && !CanFillCompletely(s, o, inst, cand, liq)) return 0;
        return FillAt(s, o, inst, o.RemainingQuantity, price, liq, FillQuality.Gap, asOf, gap: true);
    }

    // ------------------------------------------------------------------ Fill-Buchung

    private static bool CanFillCompletely(DecisionSession s, Order o, Instrument inst, List<Candidate> candidates, Liquidity liq)
    {
        if (candidates.Sum(c => c.Quantity) < o.RemainingQuantity) return false;
        if (s.Ctx.ResolveFx(inst.QuoteCurrency, s.State.BaseCurrency, s.Market) is not { } fx) return false;
        if (o.Side == OrderSide.Sell) return s.State.PositionQuantity(o.Symbol) >= o.RemainingQuantity;
        var model = s.State.FeeModel(inst.AssetClass);
        decimal notional = s.State.OrderFilledNotional.TryGetValue(o.OrderId, out var n) ? n : 0m, cost = 0;
        foreach (var c in candidates) { var g = Ledger.Gross(c.Quantity, c.Price, fx.Rate); notional += g; cost += g; }
        cost += FeeCalculator.ForFill(model, liq, 0, notional, o.FeesPaid);
        return cost <= s.State.Cash;
    }

    /// <summary>
    /// Bucht einen Fill. Begrenzung: Verkauf ≤ Bestand (kein Leerverkauf), Kauf ≤ vorhandenes Geld (Cash bleibt ≥ 0).
    /// Reicht es nicht, wird der Rest storniert. Danach OCO-Logik.
    /// </summary>
    private static decimal FillAt(DecisionSession s, Order o, Instrument inst, decimal quantity, decimal price, Liquidity liq, FillQuality quality, DateTimeOffset asOf, bool gap)
    {
        o = s.Reload(o);
        if (!o.IsOpen) return 0;
        quantity = Math.Min(quantity, o.RemainingQuantity);
        if (quantity <= 0 || price <= 0) return 0;
        if (s.Ctx.ResolveFx(inst.QuoteCurrency, s.State.BaseCurrency, s.Market) is not { } fx) return 0;
        var model = s.State.FeeModel(inst.AssetClass);
        var prevNotional = s.State.OrderFilledNotional.TryGetValue(o.OrderId, out var pn) ? pn : 0m;

        decimal FeeFor(decimal q) => FeeCalculator.ForFill(model, liq, prevNotional, Ledger.Gross(q, price, fx.Rate), o.FeesPaid);

        var wanted = quantity;
        string? shortfall = null;
        if (o.Side == OrderSide.Sell)
        {
            var pos = s.State.PositionQuantity(o.Symbol);
            if (quantity > pos) { quantity = pos; shortfall = "Bestand reicht nicht (kein Leerverkauf)"; }
        }
        else
        {
            bool Affordable(decimal q) => Ledger.Gross(q, price, fx.Rate) + FeeFor(q) <= s.State.Cash;
            if (!Affordable(quantity))
            {
                shortfall = "Nicht genug Geld";
                var step = inst.QuantityStep > 0 ? inst.QuantityStep : 0.00000001m;
                long lo = 0, hi = (long)Math.Min(Math.Floor(quantity / step), long.MaxValue / 2);
                while (lo < hi)
                {
                    var mid = lo + (hi - lo + 1) / 2;
                    if (Affordable(mid * step)) lo = mid; else hi = mid - 1;
                }
                quantity = lo * step;
            }
        }
        if (quantity > 0)
        {
            var gross = Ledger.Gross(quantity, price, fx.Rate);
            var fee = FeeFor(quantity);
            var realized = 0m;
            if (o.Side == OrderSide.Sell && s.State.Lots.TryGetValue(o.Symbol, out var lots))
                realized = Ledger.RealizedNet(gross, fee, Ledger.Consume(lots, quantity).Slices);
            var fill = new Fill
            {
                FillId = s.Ctx.Ids.NewId("fill"),
                OrderId = o.OrderId,
                AccountId = s.AccountId,
                Symbol = o.Symbol,
                Side = o.Side,
                Quantity = quantity,
                Price = price,
                Fee = fee,
                Liquidity = liq,
                Quality = quality,
                FxRate = fx.Rate,
                TimestampUtc = s.Now,
                MarketDataAsOfUtc = asOf,
                ExecutedDuringGap = gap,
                Source = o.Source,
                StrategyTag = o.StrategyTag,
                RealizedPnl = realized,
            };
            var status = o.FilledQuantity + quantity >= o.Quantity ? OrderStatus.Filled : OrderStatus.PartiallyFilled;
            s.Emit(new OrderFilled { AccountId = s.AccountId, TimestampUtc = s.Now, Fill = fill, ResultingStatus = status, FxSource = fx.Source });
            ApplyOco(s, s.Reload(o), quantity);
        }
        if (shortfall is not null) CancelRest(s, o, shortfall);
        return quantity;
    }

    /// <summary>
    /// OCO: Vollständige Ausführung storniert die anderen Orders der Gruppe; Teilausführung kürzt deren Menge um dieselbe Stückzahl.
    /// </summary>
    private static void ApplyOco(DecisionSession s, Order filled, decimal fillQuantity)
    {
        if (filled.OcoGroupId is not { } group) return;
        foreach (var other in s.State.OpenOrders.Where(x => x.OcoGroupId == group && x.OrderId != filled.OrderId).ToList())
        {
            var cur = s.Reload(other);
            if (!cur.IsOpen) continue;
            if (filled.Status == OrderStatus.Filled || cur.Quantity - fillQuantity <= cur.FilledQuantity)
                CancelOrder(s, cur, $"OCO: {filled.OrderId} ausgeführt", filled.Source);
            else
                s.Emit(new OrderModified { AccountId = s.AccountId, TimestampUtc = s.Now, OrderId = cur.OrderId, NewQuantity = cur.Quantity - fillQuantity, Source = filled.Source });
        }
    }

    // ------------------------------------------------------------------ Bewertung und Tagesverlust

    /// <summary>Bewertungskurse höchstens einmal je Intervall (Standard 1 Minute) als Event festhalten.</summary>
    private static void MaybeMark(DecisionSession s)
    {
        if (s.State.LastMarkUtc is { } lastMark && s.Now - lastMark < s.Ctx.Options.EquitySnapshotInterval) return;
        foreach (var symbol in s.State.HeldSymbols.ToList())
        {
            if (s.Market.LastPrice(symbol) is not { } price) continue;
            if (s.State.Marks.TryGetValue(symbol, out var m) && m.Price == price) continue;
            s.Emit(new MarketPriceMarked { AccountId = s.AccountId, TimestampUtc = s.Now, Symbol = symbol, Price = price });
        }
    }

    private static void CheckDailyLoss(DecisionSession s)
    {
        if (s.State.IsDailyLossLimitHit(Calendar.BerlinTime.TradingDay(s.Now))) return;
        if (CurrentDailyLoss(s.State, s.Market, s.Ctx) is { } dl && dl.Loss > 0 && dl.Loss >= dl.Limit)
            HitDailyLoss(s, dl.Loss, dl.Limit);
    }
}
