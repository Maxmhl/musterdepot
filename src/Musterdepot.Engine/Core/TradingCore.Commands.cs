using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;
using Musterdepot.Core.Events;
using Musterdepot.Engine.Calendar;
using Musterdepot.Engine.Fees;
using Musterdepot.Engine.Market;
using Musterdepot.Engine.State;

namespace Musterdepot.Engine.Core;

public static partial class TradingCore
{
    /// <summary>
    /// Decide für Befehle: prüft (inkl. Risiko-Gate) und erzeugt Events. Der Zustand wird nicht verändert;
    /// die Events werden vom Aufrufer per <see cref="Evolve"/> angewendet.
    /// </summary>
    public static CommandDecision Decide(AccountState? state, EngineCommand command, MarketState market, EngineContext ctx)
    {
        if (command is OpenAccountRequest open) return DecideOpen(state, open, ctx);
        if (state is null) return Reject($"Konto '{command.AccountId}' existiert nicht.");

        var s = new DecisionSession(state, market, ctx);
        CommandDecision result = command switch
        {
            DepositCashRequest d => Deposit(s, d),
            PlaceOrderRequest p => PlaceOrder(s, p),
            CancelOrderRequest c => Cancel(s, c),
            ModifyOrderRequest m => Modify(s, m),
            EngageKillSwitchRequest k => EngageKillSwitch(s, k),
            ReleaseKillSwitchRequest r => ReleaseKillSwitch(s, r),
            UpdateRiskConfigRequest u => UpdateRisk(s, u),
            UpdateFeeModelRequest f => UpdateFees(s, f),
            _ => Reject($"Unbekannter Befehl {command.GetType().Name}."),
        };
        return result with { Events = s.Events.ToList() };
    }

    private static CommandDecision Reject(string reason, string? rule = null) => new() { Accepted = false, RejectReason = reason, RiskRule = rule };
    private static CommandDecision Ok(string? orderId = null) => new() { Accepted = true, OrderId = orderId };

    private static CommandDecision DecideOpen(AccountState? state, OpenAccountRequest o, EngineContext ctx)
    {
        if (state is not null) return Reject($"Konto '{o.AccountId}' existiert bereits.");
        if (string.IsNullOrWhiteSpace(o.AccountId)) return Reject("AccountId fehlt.");
        if (o.InitialCash < 0) return Reject("Anfangskapital darf nicht negativ sein.");
        var e = new AccountOpened
        {
            AccountId = o.AccountId,
            TimestampUtc = ctx.Now,
            Name = o.Name,
            BaseCurrency = o.BaseCurrency,
            InitialCash = o.InitialCash,
            Risk = o.Risk ?? RiskConfig.Default,
            CryptoFees = o.CryptoFees ?? FeeModelConfig.KrakenLike,
            StockFees = o.StockFees ?? FeeModelConfig.NeoBroker,
        };
        return new CommandDecision { Accepted = true, Events = [e] };
    }

    private static CommandDecision Deposit(DecisionSession s, DepositCashRequest d)
    {
        if (d.Amount <= 0) return Reject("Einzahlung muss positiv sein.");
        s.Emit(new CashDeposited { AccountId = s.AccountId, TimestampUtc = s.Now, Amount = d.Amount, Note = d.Note });
        return Ok();
    }

    private static CommandDecision UpdateRisk(DecisionSession s, UpdateRiskConfigRequest u)
    {
        if (u.Source != CommandSource.Human) return Reject("Risikolimits darf nur ein Mensch im Programm ändern.", RiskRules.HumanOnly);
        s.Emit(new RiskConfigChanged { AccountId = s.AccountId, TimestampUtc = s.Now, Risk = u.Risk });
        return Ok();
    }

    private static CommandDecision UpdateFees(DecisionSession s, UpdateFeeModelRequest f)
    {
        s.Emit(new FeeModelChanged { AccountId = s.AccountId, TimestampUtc = s.Now, AssetClass = f.AssetClass == AssetClass.Crypto ? AssetClass.Crypto : AssetClass.Stock, Fees = f.Fees });
        return Ok();
    }

    private static CommandDecision EngageKillSwitch(DecisionSession s, EngageKillSwitchRequest k)
    {
        if (s.State.KillSwitchEngaged) return Ok();
        s.Emit(new KillSwitchEngaged { AccountId = s.AccountId, TimestampUtc = s.Now, Source = k.Source, ClientName = k.ClientName, Reason = k.Reason, OrdersCancelled = true });
        foreach (var o in s.State.OpenOrders.ToList())
        {
            var cur = s.Reload(o);
            if (cur.IsOpen)
                s.Emit(new OrderCancelled { AccountId = s.AccountId, TimestampUtc = s.Now, OrderId = cur.OrderId, Reason = "Not-Aus: " + k.Reason, Source = k.Source });
        }
        return Ok();
    }

    private static CommandDecision ReleaseKillSwitch(DecisionSession s, ReleaseKillSwitchRequest r)
    {
        if (r.Source != CommandSource.Human)
            return Reject("Den Not-Aus kann nur ein Mensch im Programm lösen.", RiskRules.KillSwitch);
        if (!s.State.KillSwitchEngaged) return Ok();
        s.Emit(new KillSwitchReleased { AccountId = s.AccountId, TimestampUtc = s.Now, ReleasedBy = string.IsNullOrWhiteSpace(r.ReleasedBy) ? "human" : r.ReleasedBy });
        return Ok();
    }

    private static CommandDecision Cancel(DecisionSession s, CancelOrderRequest c)
    {
        if (!s.State.Orders.TryGetValue(c.OrderId, out var o)) return Reject($"Order '{c.OrderId}' nicht gefunden.");
        if (!o.IsOpen) return Reject($"Order '{c.OrderId}' ist nicht mehr offen ({o.Status}).");
        CancelOrder(s, o, c.Reason, c.Source);
        return Ok(o.OrderId);
    }

    // ------------------------------------------------------------------ Modify

    private static CommandDecision Modify(DecisionSession s, ModifyOrderRequest m)
    {
        if (!s.State.Orders.TryGetValue(m.OrderId, out var o)) return Reject($"Order '{m.OrderId}' nicht gefunden.");
        if (!o.IsOpen) return Reject($"Order '{m.OrderId}' ist nicht mehr offen ({o.Status}).");
        if (s.State.KillSwitchEngaged) return Reject("Not-Aus aktiv.", RiskRules.KillSwitch);
        if (m.NewQuantity is null && m.NewLimitPrice is null && m.NewStopPrice is null) return Reject("Keine Änderung angegeben.");
        var inst = s.Ctx.Instruments.Resolve(o.Symbol);
        if (inst is null) return Reject($"Unbekanntes Symbol '{o.Symbol}'.");

        if (m.NewQuantity is { } nq)
        {
            if (nq <= o.FilledQuantity) return Reject("Neue Menge muss größer als die bereits ausgeführte Menge sein.");
            if (!Money.IsMultipleOf(nq, inst.QuantityStep)) return Reject($"Menge muss ein Vielfaches von {inst.QuantityStep} sein.");
        }
        if (m.NewLimitPrice is { } nl && (nl <= 0 || o.Type is not (OrderType.Limit or OrderType.StopLimit))) return Reject("Limit nicht änderbar/ungültig.");
        if (m.NewStopPrice is { } ns && (ns <= 0 || o.Type is not (OrderType.Stop or OrderType.StopLimit or OrderType.TrailingStop))) return Reject("Stop nicht änderbar/ungültig.");
        if (m.NewStopPrice is not null && o.IsTriggered) return Reject("Order ist bereits ausgelöst.");

        var last = s.Market.LastPrice(o.Symbol) ?? (s.State.Marks.TryGetValue(o.Symbol, out var mk) ? mk.Price : null);
        foreach (var p in new[] { m.NewLimitPrice, m.NewStopPrice })
            if (p is { } price && last is { } l && OutsideBand(price, l, s.State.Risk.PriceBandPercent))
                return Reject($"Preis {price} liegt mehr als {s.State.Risk.PriceBandPercent} % vom letzten Kurs {l} entfernt.", RiskRules.PriceBand);

        var modified = o with
        {
            Quantity = m.NewQuantity ?? o.Quantity,
            LimitPrice = m.NewLimitPrice ?? o.LimitPrice,
            StopPrice = m.NewStopPrice ?? o.StopPrice,
        };
        if (o.Side == OrderSide.Buy && modified.RemainingQuantity > 0)
        {
            var reserved = ReservedCash(s.State, s.Market, s.Ctx, excludeOrderId: o.OrderId);
            var cost = EstimatedOrderCost(s.State, modified, modified.RemainingQuantity, s.Market, s.Ctx);
            if (cost > s.State.Cash - reserved) return Reject("Nicht genug verfügbares Geld für die geänderte Order.", RiskRules.InsufficientCash);
            var notional = cost;
            if (s.State.Risk.MaxOrderNotional is { } maxN && notional > maxN)
                return Reject($"Ordervolumen {Money.Round(notional)} über Limit {maxN}.", RiskRules.MaxOrderNotional);
        }
        else if (o.Side == OrderSide.Sell && IsActive(s.State, o))
        {
            var reservedOthers = ReservedSellQuantity(s.State, o.Symbol, modified, excludeOrderId: o.OrderId);
            if (reservedOthers > s.State.PositionQuantity(o.Symbol)) return Reject("Verkauf übersteigt Bestand (kein Leerverkauf).", RiskRules.NoShortSelling);
        }

        s.Emit(new OrderModified { AccountId = s.AccountId, TimestampUtc = s.Now, OrderId = o.OrderId, NewQuantity = m.NewQuantity, NewLimitPrice = m.NewLimitPrice, NewStopPrice = m.NewStopPrice, Source = m.Source });
        if (m.NewQuantity is { } q && o.BracketRole == BracketRole.Entry)
            foreach (var child in ChildrenOf(s.State, o.OrderId).Where(c => c.IsOpen))
                s.Emit(new OrderModified { AccountId = s.AccountId, TimestampUtc = s.Now, OrderId = child.OrderId, NewQuantity = q, Source = m.Source });
        return Ok(o.OrderId);
    }

    // ------------------------------------------------------------------ PlaceOrder + Risiko-Gate

    private static bool OutsideBand(decimal price, decimal reference, decimal bandPercent)
        => reference > 0 && Math.Abs(price - reference) / reference * 100m > bandPercent;

    private static CommandDecision PlaceOrder(DecisionSession s, PlaceOrderRequest r)
    {
        // Idempotenz: gleiche ClientOrderId → bestehende Order, kein Duplikat.
        if (!string.IsNullOrWhiteSpace(r.ClientOrderId) && s.State.FindByClientOrderId(r.ClientOrderId) is { } existing)
            return Ok(existing.OrderId);

        var check = CheckOrder(s, r, emitSideEffects: true);
        if (check.RejectReason is not null)
        {
            s.Emit(new OrderRejected
            {
                AccountId = s.AccountId,
                TimestampUtc = s.Now,
                OrderId = s.Ctx.Ids.NewId("ord"),
                ClientOrderId = r.ClientOrderId ?? "",
                Symbol = r.Symbol ?? "",
                Reason = check.RejectReason,
                RiskRule = check.RiskRule,
                Source = r.Source,
            });
            return Reject(check.RejectReason, check.RiskRule);
        }

        var inst = check.Instrument!;
        var orderId = s.Ctx.Ids.NewId("ord");
        var eligible = s.Now + s.Ctx.Execution.Latency;
        DateTimeOffset? expires = r.TimeInForce switch
        {
            TimeInForce.Day => DayExpiry(inst, s.Now, s.Ctx),
            TimeInForce.Gtd => r.ExpiresUtc,
            _ => null,
        };
        var hasBracket = r.TakeProfitPrice is not null || r.StopLossPrice is not null;
        var order = new Order
        {
            OrderId = orderId,
            ClientOrderId = r.ClientOrderId!,
            AccountId = s.AccountId,
            Symbol = inst.Symbol,
            Side = r.Side,
            Type = r.Type,
            TimeInForce = r.TimeInForce,
            Quantity = check.Quantity,
            LimitPrice = r.Type is OrderType.Limit or OrderType.StopLimit ? r.LimitPrice : null,
            StopPrice = r.Type == OrderType.TrailingStop ? check.TrailStop : r.Type is OrderType.Stop or OrderType.StopLimit ? r.StopPrice : null,
            TrailAmount = r.Type == OrderType.TrailingStop ? r.TrailAmount : null,
            TrailPercent = r.Type == OrderType.TrailingStop ? r.TrailPercent : null,
            TrailReferencePrice = r.Type == OrderType.TrailingStop ? check.ReferencePrice : null,
            ExpiresUtc = expires,
            Source = r.Source,
            ClientName = r.ClientName,
            StrategyTag = r.StrategyTag,
            OcoGroupId = r.OcoGroupId,
            BracketRole = hasBracket ? BracketRole.Entry : BracketRole.None,
            CreatedUtc = s.Now,
            EligibleFromUtc = eligible,
            Status = OrderStatus.PendingNew,
            LastUpdateUtc = s.Now,
        };
        s.Emit(new OrderSubmitted { AccountId = s.AccountId, TimestampUtc = s.Now, Order = order });

        if (hasBracket)
        {
            var group = $"bracket:{orderId}";
            if (r.TakeProfitPrice is { } tp)
                s.Emit(new OrderSubmitted
                {
                    AccountId = s.AccountId,
                    TimestampUtc = s.Now,
                    Order = order with
                    {
                        OrderId = s.Ctx.Ids.NewId("ord"),
                        ClientOrderId = r.ClientOrderId + ":tp",
                        Side = OrderSide.Sell,
                        Type = OrderType.Limit,
                        TimeInForce = TimeInForce.Gtc,
                        LimitPrice = tp,
                        StopPrice = null,
                        ExpiresUtc = null,
                        OcoGroupId = group,
                        ParentOrderId = orderId,
                        BracketRole = BracketRole.TakeProfit,
                    },
                });
            if (r.StopLossPrice is { } sl)
                s.Emit(new OrderSubmitted
                {
                    AccountId = s.AccountId,
                    TimestampUtc = s.Now,
                    Order = order with
                    {
                        OrderId = s.Ctx.Ids.NewId("ord"),
                        ClientOrderId = r.ClientOrderId + ":sl",
                        Side = OrderSide.Sell,
                        Type = OrderType.Stop,
                        TimeInForce = TimeInForce.Gtc,
                        LimitPrice = null,
                        StopPrice = sl,
                        ExpiresUtc = null,
                        OcoGroupId = group,
                        ParentOrderId = orderId,
                        BracketRole = BracketRole.StopLoss,
                    },
                });
        }
        return Ok(orderId);
    }

    /// <summary>Ablauf einer DAY-Order: Handelsschluss (Aktien) bzw. nächste Mitternacht Europe/Berlin (24/7-Märkte).</summary>
    public static DateTimeOffset DayExpiry(Instrument inst, DateTimeOffset now, EngineContext ctx)
        => ctx.Calendar.SessionCloseUtc(inst, now) ?? BerlinTime.NextMidnightUtc(now);

    internal sealed record OrderCheck
    {
        public string? RejectReason { get; init; }
        public string? RiskRule { get; init; }
        public Instrument? Instrument { get; init; }
        public decimal Quantity { get; init; }
        public decimal? ReferencePrice { get; init; }
        public decimal? EstimatedPrice { get; init; }
        public decimal Notional { get; init; }
        public decimal EstimatedFee { get; init; }
        public decimal FxRate { get; init; } = 1m;
        public decimal? TrailStop { get; init; }
        public bool PriceIsDelayed { get; init; }
        public List<string> Warnings { get; init; } = [];
    }

    private static OrderCheck Fail(string reason, string? rule = null, OrderCheck? partial = null)
        => (partial ?? new OrderCheck()) with { RejectReason = reason, RiskRule = rule };

    /// <summary>
    /// Validierung + Risiko-Gate (Reihenfolge: Eingaben, Not-Aus, erlaubte Symbole, Rate, Marktdaten/Handelszeit, Menge,
    /// Preisband, Bracket, offene Orders, Leerverkauf; für erhöhende Orders zusätzlich Tagesverlust, Order-/Positionsgröße, Deckung).
    /// </summary>
    internal static OrderCheck CheckOrder(DecisionSession s, PlaceOrderRequest r, bool emitSideEffects)
    {
        var risk = s.State.Risk;
        var ctx = s.Ctx;
        if (string.IsNullOrWhiteSpace(r.ClientOrderId)) return Fail("ClientOrderId fehlt.");
        if (string.IsNullOrWhiteSpace(r.Symbol)) return Fail("Symbol fehlt.");
        var inst = ctx.Instruments.Resolve(r.Symbol);
        if (inst is null) return Fail($"Unbekanntes Symbol '{r.Symbol}'.");
        var chk = new OrderCheck { Instrument = inst };

        // Eingaben
        switch (r.Type)
        {
            case OrderType.Limit when r.LimitPrice is not > 0:
                return Fail("Limit-Order braucht einen positiven Limitpreis.", partial: chk);
            case OrderType.Stop when r.StopPrice is not > 0:
                return Fail("Stop-Order braucht einen positiven Stop-Preis.", partial: chk);
            case OrderType.StopLimit when r.StopPrice is not > 0 || r.LimitPrice is not > 0:
                return Fail("Stop-Limit-Order braucht Stop- und Limitpreis.", partial: chk);
            case OrderType.TrailingStop when (r.TrailAmount is null) == (r.TrailPercent is null):
                return Fail("Trailing-Stop braucht genau einen Abstand (Betrag oder Prozent).", partial: chk);
            case OrderType.TrailingStop when r.TrailAmount is <= 0 || r.TrailPercent is <= 0 or >= 100:
                return Fail("Trailing-Abstand ungültig.", partial: chk);
        }
        if ((r.Quantity is null) == (r.Notional is null)) return Fail("Genau eine Angabe: Stückzahl oder Betrag.", partial: chk);
        if (r.Quantity is <= 0 || r.Notional is <= 0) return Fail("Stückzahl/Betrag muss positiv sein.", partial: chk);
        if (r.TimeInForce == TimeInForce.Gtd && (r.ExpiresUtc is null || r.ExpiresUtc <= s.Now))
            return Fail("GTD braucht ein Ablaufdatum in der Zukunft.", partial: chk);
        var hasBracket = r.TakeProfitPrice is not null || r.StopLossPrice is not null;
        if (hasBracket && r.Side != OrderSide.Buy) return Fail("Bracket nur mit Kauf-Einstieg (kein Leerverkauf).", partial: chk);
        if (hasBracket && r.Type == OrderType.TrailingStop) return Fail("Bracket mit Trailing-Stop-Einstieg wird nicht unterstützt.", partial: chk);

        // Not-Aus, Symbole, Rate
        if (s.State.KillSwitchEngaged) return Fail("Not-Aus aktiv – keine neuen Orders.", RiskRules.KillSwitch, chk);
        if (risk.AllowedSymbols is { Count: > 0 } allowed && !allowed.Contains(inst.Symbol, StringComparer.OrdinalIgnoreCase))
            return Fail($"Symbol '{inst.Symbol}' ist nicht erlaubt.", RiskRules.AllowedSymbols, chk);
        var perMinute = r.Source == CommandSource.Ai ? risk.MaxOrdersPerMinuteAi : risk.MaxOrdersPerMinuteHuman;
        var recent = s.State.RecentSubmissions.Count(x => x.Source == r.Source && x.TimestampUtc > s.Now - TimeSpan.FromMinutes(1));
        if (recent >= perMinute) return Fail($"Mehr als {perMinute} Orders pro Minute ({r.Source}).", RiskRules.MaxOrdersPerMinute, chk);

        // Marktdaten, Handelszeit
        var sym = s.Market.Get(inst.Symbol);
        var last = sym?.LastPrice ?? (s.State.Marks.TryGetValue(inst.Symbol, out var mark) ? mark.Price : null);
        chk = chk with { PriceIsDelayed = sym?.IsDelayed ?? inst.AssetClass != AssetClass.Crypto };
        if (last is null) return Fail($"Kein Kurs für '{inst.Symbol}' verfügbar.", RiskRules.NoMarketData, chk);
        var isOpen = ctx.Calendar.IsOpen(inst, s.Now);
        if (r.Type == OrderType.Market)
        {
            if (!isOpen) return Fail("Markt geschlossen – Market-Orders nur während der Handelszeit.", RiskRules.MarketHours, chk);
            var age = sym?.LastAsOfUtc is { } asOf ? s.Now - asOf : TimeSpan.MaxValue;
            var allowedAge = AllowedQuoteAge(sym, risk, ctx);
            if (age > allowedAge) return Fail($"Letzter Kurs zu alt ({(age == TimeSpan.MaxValue ? "unbekannt" : age.ToString())}, erlaubt {allowedAge}).", RiskRules.MaxQuoteAge, chk);
        }
        else if (!isOpen) chk.Warnings.Add("Markt geschlossen – Order wartet auf die nächste Handelszeit.");
        if (chk.PriceIsDelayed) chk.Warnings.Add("Kurse verzögert – Ausführung erst gegen Trades nach dem Ordereingang.");

        var fx = ctx.ResolveFx(inst.QuoteCurrency, s.State.BaseCurrency, s.Market);
        if (fx is null) return Fail($"Kein Umrechnungskurs {inst.QuoteCurrency}→{s.State.BaseCurrency}.", RiskRules.FxUnavailable, chk);
        chk = chk with { FxRate = fx.Value.Rate };

        // Referenzpreis
        decimal? trailStop = null;
        if (r.Type == OrderType.TrailingStop)
        {
            var dist = r.TrailAmount ?? last.Value * r.TrailPercent!.Value / 100m;
            trailStop = r.Side == OrderSide.Sell
                ? Money.RoundToTick(last.Value - dist, inst.TickSize, up: false)
                : Money.RoundToTick(last.Value + dist, inst.TickSize, up: true);
            if (trailStop <= 0) return Fail("Trailing-Abstand größer als Kurs.", partial: chk);
        }
        decimal refPrice = r.Type switch
        {
            OrderType.Limit or OrderType.StopLimit => r.LimitPrice!.Value,
            OrderType.Stop => r.StopPrice!.Value,
            OrderType.TrailingStop => trailStop!.Value,
            _ => MarketReference(sym, r.Side, ctx) ?? last.Value,
        };
        decimal? estimated = refPrice;
        if (r.Type == OrderType.Market && sym is { IsDelayed: false })
        {
            var book = r.Side == OrderSide.Buy ? sym.BookAsks : sym.BookBids;
            if (book.Count > 0 && r.Quantity is { } q0)
            {
                decimal left = q0, cost = 0;
                foreach (var (p, qty) in book) { var take = Math.Min(left, qty); cost += take * p; left -= take; if (left <= 0) break; }
                if (left > 0) chk.Warnings.Add("Orderbuch reicht nicht für die ganze Menge – Rest wird später ausgeführt.");
                if (q0 - left > 0) estimated = Money.Normalize(Math.Round(cost / (q0 - left), 10));
            }
        }

        // Menge
        decimal quantity;
        if (r.Notional is { } notional)
        {
            quantity = Money.FloorToStep(notional / (refPrice * fx.Value.Rate), inst.QuantityStep);
            if (quantity <= 0 || quantity < inst.MinQuantity) return Fail("Betrag zu klein für die kleinste handelbare Menge.", partial: chk);
        }
        else
        {
            quantity = r.Quantity!.Value;
            if (!Money.IsMultipleOf(quantity, inst.QuantityStep)) return Fail($"Menge muss ein Vielfaches von {inst.QuantityStep} sein.", partial: chk);
            if (quantity < inst.MinQuantity) return Fail($"Mindestmenge {inst.MinQuantity}.", partial: chk);
        }
        var orderNotional = Ledger.Gross(quantity, estimated ?? refPrice, fx.Value.Rate);
        var model = s.State.FeeModel(inst.AssetClass);
        var liq = r.Type == OrderType.Limit ? Liquidity.Maker : Liquidity.Taker;
        if (r.Type == OrderType.Limit && MarketReference(sym, r.Side, ctx) is { } mref && (r.Side == OrderSide.Buy ? mref <= r.LimitPrice : mref >= r.LimitPrice))
            liq = Liquidity.Taker;
        chk = chk with
        {
            Quantity = quantity,
            ReferencePrice = last,
            EstimatedPrice = estimated,
            Notional = orderNotional,
            EstimatedFee = FeeCalculator.Total(model, orderNotional, liq),
            TrailStop = trailStop,
        };

        // Preisband (Limit/Stop/Bracket-Preise gegen letzten Kurs)
        foreach (var p in new[] { r.Type is OrderType.Limit or OrderType.StopLimit ? r.LimitPrice : null, r.Type is OrderType.Stop or OrderType.StopLimit ? r.StopPrice : null, r.TakeProfitPrice, r.StopLossPrice })
            if (p is { } price && OutsideBand(price, last.Value, risk.PriceBandPercent))
                return Fail($"Preis {price} liegt mehr als {risk.PriceBandPercent} % vom letzten Kurs {last} entfernt.", RiskRules.PriceBand, chk);

        if (hasBracket)
        {
            var entryRef = r.LimitPrice ?? r.StopPrice ?? last.Value;
            if (r.TakeProfitPrice is { } tp && tp <= entryRef) return Fail("Take-Profit muss über dem Einstieg liegen.", partial: chk);
            if (r.StopLossPrice is { } sl && (sl >= entryRef || sl <= 0)) return Fail("Stop-Loss muss unter dem Einstieg liegen.", partial: chk);
        }

        // Offene Orders
        var newOrders = 1 + (r.TakeProfitPrice is null ? 0 : 1) + (r.StopLossPrice is null ? 0 : 1);
        var openCount = s.State.OpenOrders.Count();
        if (openCount + newOrders > risk.MaxOpenOrders)
            return Fail($"Zu viele offene Orders ({openCount}/{risk.MaxOpenOrders}).", RiskRules.MaxOpenOrders, chk);

        if (r.Side == OrderSide.Sell)
        {
            // Kein Leerverkauf: Menge ≤ Bestand abzüglich bereits durch Verkaufsorders gebundener Stücke.
            var probe = new Order
            {
                OrderId = "__new__", ClientOrderId = r.ClientOrderId, AccountId = s.AccountId, Symbol = inst.Symbol, Side = OrderSide.Sell,
                Type = r.Type, Quantity = quantity, Source = r.Source, CreatedUtc = s.Now, OcoGroupId = r.OcoGroupId,
            };
            var position = s.State.PositionQuantity(inst.Symbol);
            if (ReservedSellQuantity(s.State, inst.Symbol, probe) > position)
                return Fail($"Verkauf von {quantity} übersteigt den verfügbaren Bestand {position - ReservedSellQuantity(s.State, inst.Symbol)} (kein Leerverkauf).", RiskRules.NoShortSelling, chk);
            return chk;
        }

        // Erhöhende Orders (Käufe): Tagesverlust, Größen, Deckung
        var day = BerlinTime.TradingDay(s.Now);
        if (s.State.IsDailyLossLimitHit(day)) return Fail("Tagesverlustgrenze erreicht – nur noch reduzierende Orders.", RiskRules.MaxDailyLoss, chk);
        if (CurrentDailyLoss(s.State, s.Market, ctx) is { } dl && dl.Loss > 0 && dl.Loss >= dl.Limit)
        {
            if (emitSideEffects) HitDailyLoss(s, dl.Loss, dl.Limit);
            return Fail("Tagesverlustgrenze erreicht – nur noch reduzierende Orders.", RiskRules.MaxDailyLoss, chk);
        }
        var equity = LiveEquity(s.State, s.Market, ctx);
        if (risk.MaxOrderNotional is { } maxOrder && orderNotional > maxOrder)
            return Fail($"Ordervolumen {orderNotional} über Limit {maxOrder}.", RiskRules.MaxOrderNotional, chk);
        if (risk.MaxOrderPercentOfEquity is { } maxOrderPct && orderNotional > equity * maxOrderPct / 100m)
            return Fail($"Ordervolumen {orderNotional} über {maxOrderPct} % des Depotwerts ({equity}).", RiskRules.MaxOrderPercentOfEquity, chk);

        var openBuyQty = s.State.OpenOrders.Where(o => o.Symbol == inst.Symbol && o.Side == OrderSide.Buy).Sum(o => o.RemainingQuantity);
        var positionValue = Ledger.Gross(s.State.PositionQuantity(inst.Symbol) + openBuyQty, last.Value, fx.Value.Rate) + orderNotional;
        if (risk.MaxPositionNotional is { } maxPos && positionValue > maxPos)
            return Fail($"Positionsgröße {positionValue} über Limit {maxPos}.", RiskRules.MaxPositionNotional, chk);
        if (risk.MaxPositionPercentOfEquity is { } maxPosPct && positionValue > equity * maxPosPct / 100m)
            return Fail($"Positionsgröße {positionValue} über {maxPosPct} % des Depotwerts ({equity}).", RiskRules.MaxPositionPercentOfEquity, chk);

        var reserved = ReservedCash(s.State, s.Market, ctx);
        var fee = Math.Max(chk.EstimatedFee, FeeCalculator.Total(model, orderNotional, Liquidity.Taker));
        if (orderNotional + fee > s.State.Cash - reserved)
            return Fail($"Nicht genug verfügbares Geld: benötigt {orderNotional + fee}, verfügbar {s.State.Cash - reserved}.", RiskRules.InsufficientCash, chk);
        return chk;
    }

    /// <summary>Vorschau (dry_run): gleiche Prüfung wie <c>Decide</c>, ohne Events.</summary>
    public static OrderPreview Preview(AccountState? state, PlaceOrderRequest r, MarketState market, EngineContext ctx)
    {
        if (state is null) return new OrderPreview { WouldBeAccepted = false, RejectReason = $"Konto '{r.AccountId}' existiert nicht." };
        if (!string.IsNullOrWhiteSpace(r.ClientOrderId) && state.FindByClientOrderId(r.ClientOrderId) is { } existing)
            return new OrderPreview { WouldBeAccepted = true, Quantity = existing.Quantity, Warnings = ["Order mit dieser ClientOrderId existiert bereits."] };
        var s = new DecisionSession(state, market, ctx);
        var chk = CheckOrder(s, r, emitSideEffects: false);
        var cashAfter = chk.Instrument is null ? (decimal?)null
            : r.Side == OrderSide.Buy ? state.Cash - chk.Notional - chk.EstimatedFee : state.Cash + chk.Notional - chk.EstimatedFee;
        return new OrderPreview
        {
            WouldBeAccepted = chk.RejectReason is null,
            RejectReason = chk.RejectReason,
            RiskRule = chk.RiskRule,
            Quantity = chk.Quantity,
            EstimatedPrice = chk.EstimatedPrice,
            EstimatedNotional = chk.Quantity > 0 ? chk.Notional : null,
            EstimatedFee = chk.EstimatedFee,
            EstimatedCashAfter = chk.Quantity > 0 ? cashAfter : null,
            PriceIsDelayed = chk.PriceIsDelayed,
            Warnings = chk.Warnings,
        };
    }

    internal static IEnumerable<Order> ChildrenOf(AccountState s, string parentId)
        => s.OrderSequence.Select(id => s.Orders[id]).Where(o => o.ParentOrderId == parentId);

    /// <summary>Storniert eine Order inkl. Bracket-Folgen.</summary>
    internal static void CancelOrder(DecisionSession s, Order o, string reason, CommandSource source)
    {
        s.Emit(new OrderCancelled { AccountId = s.AccountId, TimestampUtc = s.Now, OrderId = o.OrderId, Reason = reason, Source = source });
        AfterEntryClosed(s, s.Reload(o), source);
    }

    internal static void ExpireOrder(DecisionSession s, Order o)
    {
        s.Emit(new OrderExpired { AccountId = s.AccountId, TimestampUtc = s.Now, OrderId = o.OrderId });
        AfterEntryClosed(s, s.Reload(o), o.Source);
    }

    /// <summary>
    /// Bracket: Einstieg ohne Ausführung beendet → Kinder stornieren; teilweise ausgeführt → Kinder auf ausgeführte Menge kürzen
    /// (sie werden damit aktiv).
    /// </summary>
    private static void AfterEntryClosed(DecisionSession s, Order entry, CommandSource source)
    {
        if (entry.BracketRole != BracketRole.Entry || entry.IsOpen) return;
        foreach (var child in ChildrenOf(s.State, entry.OrderId).Where(c => c.IsOpen).ToList())
        {
            if (entry.FilledQuantity <= 0)
                s.Emit(new OrderCancelled { AccountId = s.AccountId, TimestampUtc = s.Now, OrderId = child.OrderId, Reason = "Bracket-Einstieg nicht ausgeführt", Source = source });
            else if (child.Quantity != entry.FilledQuantity)
                s.Emit(new OrderModified { AccountId = s.AccountId, TimestampUtc = s.Now, OrderId = child.OrderId, NewQuantity = entry.FilledQuantity, Source = source });
        }
    }

    internal static void HitDailyLoss(DecisionSession s, decimal loss, decimal limit)
    {
        if (s.State.IsDailyLossLimitHit(BerlinTime.TradingDay(s.Now))) return;
        s.Emit(new DailyLossLimitHit { AccountId = s.AccountId, TimestampUtc = s.Now, Loss = Money.Round(loss), Limit = limit });
        // Danach nur noch reduzierende Orders: offene Kauforders werden storniert, Schutz-Verkäufe bleiben.
        foreach (var o in s.State.OpenOrders.Where(o => o.Side == OrderSide.Buy).ToList())
            CancelOrder(s, s.Reload(o), "Tagesverlustgrenze erreicht", CommandSource.Human);
    }
}
