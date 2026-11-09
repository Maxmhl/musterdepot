using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;
using Musterdepot.Core.Events;
using Musterdepot.Core.MarketData;

namespace Musterdepot.Commands.Tests.Fakes;

/// <summary>Einfache Engine für Tests: Decide/Evolve für die wichtigsten Befehle, ohne Fill-Simulation.</summary>
public sealed class FakeTradingEngine(IClock clock) : ITradingEngine
{
    private sealed class AccountState
    {
        public required Account Account { get; set; }
        public decimal Cash { get; set; }
        public RiskConfig Risk { get; set; } = RiskConfig.Default;
        public FeeModelConfig CryptoFees { get; set; } = FeeModelConfig.KrakenLike;
        public FeeModelConfig StockFees { get; set; } = FeeModelConfig.NeoBroker;
        public bool KillSwitch { get; set; }
        public bool DailyLoss { get; set; }
        public Dictionary<string, Order> Orders { get; } = [];
        public List<Fill> Fills { get; } = [];
        public List<DomainEvent> Events { get; } = [];
        public Dictionary<string, decimal> Positions { get; } = [];
    }

    private readonly Lock _lock = new();
    private readonly Dictionary<string, AccountState> _accounts = [];
    private int _orderNo;

    public event EventHandler<EngineEventsEventArgs>? EventsAppended;

    public List<EngineCommand> Executed { get; } = [];
    public int PreviewCount { get; private set; }
    public List<DateTimeOffset> Ticks { get; } = [];
    public List<MarketEvent> MarketEvents { get; } = [];
    public int LoadedEventCount { get; private set; }

    public void LoadFromEvents(IEnumerable<DomainEvent> events)
    {
        lock (_lock)
            foreach (var e in events)
            {
                Evolve(e);
                LoadedEventCount++;
            }
    }

    public IReadOnlyList<DomainEvent> GetEvents(string accountId, long afterSequence = 0)
    {
        lock (_lock) return _accounts.TryGetValue(accountId, out var a) ? a.Events.Where(e => e.Sequence > afterSequence).ToList() : [];
    }

    public EngineResult Execute(EngineCommand command)
    {
        EngineResult result;
        lock (_lock)
        {
            Executed.Add(command);
            result = Decide(command);
            foreach (var e in result.Events) Evolve(e);
        }
        if (result.Events.Count > 0) EventsAppended?.Invoke(this, new EngineEventsEventArgs(result.Events));
        return result;
    }

    /// <summary>Löst die Tagesverlustgrenze aus (wie die echte Engine nach einem Verlust).</summary>
    public void TriggerDailyLoss(string accountId, decimal loss = 1500m) => Emit(accountId, (acc, seq) => [new DailyLossLimitHit { AccountId = acc, Sequence = seq, TimestampUtc = clock.UtcNow, Loss = loss, Limit = 1000m }]);

    /// <summary>Füllt eine Order vollständig.</summary>
    public void FillOrder(string accountId, string orderId, decimal price, decimal fee = 1m)
    {
        Order order;
        lock (_lock) order = _accounts[accountId].Orders[orderId];
        Emit(accountId, (acc, seq) =>
        [
            new OrderFilled
            {
                AccountId = acc, Sequence = seq, TimestampUtc = clock.UtcNow, ResultingStatus = OrderStatus.Filled,
                Fill = new Fill
                {
                    FillId = $"fill_{orderId}", OrderId = orderId, AccountId = acc, Symbol = order.Symbol, Side = order.Side,
                    Quantity = order.Quantity, Price = price, Fee = fee, TimestampUtc = clock.UtcNow, Source = order.Source,
                },
            },
        ]);
    }

    private void Emit(string accountId, Func<string, long, IReadOnlyList<DomainEvent>> make)
    {
        IReadOnlyList<DomainEvent> events;
        lock (_lock)
        {
            events = make(accountId, NextSeq(accountId));
            foreach (var e in events) Evolve(e);
        }
        EventsAppended?.Invoke(this, new EngineEventsEventArgs(events));
    }

    private long NextSeq(string accountId) => _accounts.TryGetValue(accountId, out var a) && a.Events.Count > 0 ? a.Events[^1].Sequence + 1 : 1;

    private EngineResult Decide(EngineCommand command)
    {
        var now = clock.UtcNow;
        var acc = command.AccountId;
        var seq = NextSeq(acc);
        var list = new List<DomainEvent>();
        if (command is not OpenAccountRequest && !_accounts.ContainsKey(acc)) return EngineResult.Reject("Konto unbekannt");
        switch (command)
        {
            case OpenAccountRequest o:
                if (_accounts.ContainsKey(acc)) return EngineResult.Reject("Konto existiert");
                list.Add(new AccountOpened
                {
                    AccountId = acc, Sequence = seq, TimestampUtc = now, Name = o.Name, InitialCash = o.InitialCash,
                    BaseCurrency = o.BaseCurrency, Risk = o.Risk ?? RiskConfig.Default,
                    CryptoFees = o.CryptoFees ?? FeeModelConfig.KrakenLike, StockFees = o.StockFees ?? FeeModelConfig.NeoBroker,
                });
                break;
            case DepositCashRequest d:
                list.Add(new CashDeposited { AccountId = acc, Sequence = seq, TimestampUtc = now, Amount = d.Amount, Note = d.Note });
                break;
            case PlaceOrderRequest p:
            {
                var a = _accounts[acc];
                if (a.KillSwitch) return EngineResult.Reject("Not-Aus aktiv", "KillSwitch");
                var id = $"ord_{++_orderNo:D4}";
                var order = new Order
                {
                    OrderId = id, ClientOrderId = p.ClientOrderId, AccountId = acc, Symbol = p.Symbol, Side = p.Side, Type = p.Type,
                    TimeInForce = p.TimeInForce, Quantity = p.Quantity ?? (p.Notional ?? 0) / 100m, LimitPrice = p.LimitPrice,
                    StopPrice = p.StopPrice, Source = p.Source, ClientName = p.ClientName, StrategyTag = p.StrategyTag,
                    CreatedUtc = now, Status = OrderStatus.PendingNew,
                };
                list.Add(new OrderSubmitted { AccountId = acc, Sequence = seq, TimestampUtc = now, Order = order });
                list.Add(new OrderAccepted { AccountId = acc, Sequence = seq + 1, TimestampUtc = now, OrderId = id });
                return new EngineResult { Accepted = true, Events = list, OrderId = id };
            }
            case CancelOrderRequest c:
            {
                if (!_accounts[acc].Orders.TryGetValue(c.OrderId, out var o) || !o.IsOpen) return EngineResult.Reject("Order nicht offen");
                list.Add(new OrderCancelled { AccountId = acc, Sequence = seq, TimestampUtc = now, OrderId = c.OrderId, Reason = c.Reason, Source = c.Source });
                return new EngineResult { Accepted = true, Events = list, OrderId = c.OrderId };
            }
            case ModifyOrderRequest m:
            {
                if (!_accounts[acc].Orders.TryGetValue(m.OrderId, out var o) || !o.IsOpen) return EngineResult.Reject("Order nicht offen");
                list.Add(new OrderModified { AccountId = acc, Sequence = seq, TimestampUtc = now, OrderId = m.OrderId, NewQuantity = m.NewQuantity, NewLimitPrice = m.NewLimitPrice, NewStopPrice = m.NewStopPrice, Source = m.Source });
                return new EngineResult { Accepted = true, Events = list, OrderId = m.OrderId };
            }
            case EngageKillSwitchRequest k:
            {
                list.Add(new KillSwitchEngaged { AccountId = acc, Sequence = seq, TimestampUtc = now, Source = k.Source, ClientName = k.ClientName, Reason = k.Reason });
                foreach (var o in _accounts[acc].Orders.Values.Where(o => o.IsOpen))
                    list.Add(new OrderCancelled { AccountId = acc, Sequence = seq + list.Count, TimestampUtc = now, OrderId = o.OrderId, Reason = "kill_switch", Source = k.Source });
                break;
            }
            case ReleaseKillSwitchRequest r:
                if (r.Source != CommandSource.Human) return EngineResult.Reject("Nur Mensch");
                list.Add(new KillSwitchReleased { AccountId = acc, Sequence = seq, TimestampUtc = now, ReleasedBy = r.ReleasedBy });
                break;
            case UpdateRiskConfigRequest u:
                list.Add(new RiskConfigChanged { AccountId = acc, Sequence = seq, TimestampUtc = now, Risk = u.Risk });
                break;
            case UpdateFeeModelRequest f:
                list.Add(new FeeModelChanged { AccountId = acc, Sequence = seq, TimestampUtc = now, AssetClass = f.AssetClass, Fees = f.Fees });
                break;
            default:
                return EngineResult.Reject("Unbekannt");
        }
        return new EngineResult { Accepted = true, Events = list };
    }

    private void Evolve(DomainEvent e)
    {
        if (e is AccountOpened o)
        {
            _accounts[o.AccountId] = new AccountState
            {
                Account = new Account { AccountId = o.AccountId, Name = o.Name, BaseCurrency = o.BaseCurrency, CreatedUtc = o.TimestampUtc, InitialCash = o.InitialCash },
                Cash = o.InitialCash, Risk = o.Risk, CryptoFees = o.CryptoFees, StockFees = o.StockFees,
            };
        }
        if (!_accounts.TryGetValue(e.AccountId, out var a)) return;
        a.Events.Add(e);
        switch (e)
        {
            case CashDeposited d: a.Cash += d.Amount; break;
            case OrderSubmitted s: a.Orders[s.Order.OrderId] = s.Order; break;
            case OrderAccepted ac: a.Orders[ac.OrderId] = a.Orders[ac.OrderId] with { Status = OrderStatus.Working }; break;
            case OrderCancelled c: a.Orders[c.OrderId] = a.Orders[c.OrderId] with { Status = OrderStatus.Cancelled }; break;
            case OrderModified m:
            {
                var o2 = a.Orders[m.OrderId];
                a.Orders[m.OrderId] = o2 with { Quantity = m.NewQuantity ?? o2.Quantity, LimitPrice = m.NewLimitPrice ?? o2.LimitPrice, StopPrice = m.NewStopPrice ?? o2.StopPrice };
                break;
            }
            case OrderFilled f:
            {
                var fill = f.Fill;
                a.Fills.Add(fill);
                a.Orders[fill.OrderId] = a.Orders[fill.OrderId] with { Status = f.ResultingStatus, FilledQuantity = fill.Quantity, AveragePrice = fill.Price };
                var sign = fill.Side == OrderSide.Buy ? 1 : -1;
                a.Cash -= sign * fill.Quantity * fill.Price + fill.Fee;
                a.Positions[fill.Symbol] = a.Positions.GetValueOrDefault(fill.Symbol) + sign * fill.Quantity;
                break;
            }
            case KillSwitchEngaged: a.KillSwitch = true; break;
            case KillSwitchReleased: a.KillSwitch = false; break;
            case DailyLossLimitHit: a.DailyLoss = true; break;
            case RiskConfigChanged r: a.Risk = r.Risk; break;
            case FeeModelChanged fc when fc.AssetClass == AssetClass.Crypto: a.CryptoFees = fc.Fees; break;
            case FeeModelChanged fc: a.StockFees = fc.Fees; break;
        }
    }

    public OrderPreview Preview(PlaceOrderRequest request)
    {
        lock (_lock)
        {
            PreviewCount++;
            var ks = _accounts.TryGetValue(request.AccountId, out var a) && a.KillSwitch;
            return new OrderPreview { WouldBeAccepted = !ks, RejectReason = ks ? "Not-Aus" : null, Quantity = request.Quantity ?? 0, EstimatedPrice = 100m, EstimatedFee = 1m };
        }
    }

    public void OnMarketEvent(MarketEvent marketEvent) { lock (_lock) MarketEvents.Add(marketEvent); }
    public void OnClockTick(DateTimeOffset utcNow) { lock (_lock) Ticks.Add(utcNow); }

    public bool AccountExists(string accountId) { lock (_lock) return _accounts.ContainsKey(accountId); }
    public IReadOnlyList<Account> GetAccounts() { lock (_lock) return _accounts.Values.Select(a => a.Account).ToList(); }

    public PortfolioSnapshot GetPortfolio(string accountId)
    {
        lock (_lock)
        {
            var a = _accounts[accountId];
            return new PortfolioSnapshot
            {
                AccountId = accountId, AsOfUtc = clock.UtcNow, Cash = a.Cash, PositionsValue = 0,
                KillSwitchEngaged = a.KillSwitch, DailyLossLimitHit = a.DailyLoss,
                Positions = a.Positions.Where(p => p.Value != 0).Select(p => new PositionSnapshot { Symbol = p.Key, Quantity = p.Value, AverageCost = 0, CostBasis = 0 }).ToList(),
            };
        }
    }

    public IReadOnlyList<Order> GetOrders(string accountId, bool openOnly = false)
    {
        lock (_lock) return _accounts.TryGetValue(accountId, out var a) ? a.Orders.Values.Where(o => !openOnly || o.IsOpen).ToList() : [];
    }

    public Order? GetOrder(string accountId, string orderId) { lock (_lock) return _accounts.TryGetValue(accountId, out var a) ? a.Orders.GetValueOrDefault(orderId) : null; }

    public Order? GetOrderByClientOrderId(string accountId, string clientOrderId)
    {
        lock (_lock) return _accounts.TryGetValue(accountId, out var a) ? a.Orders.Values.FirstOrDefault(o => o.ClientOrderId == clientOrderId) : null;
    }

    public IReadOnlyList<Fill> GetFills(string accountId, DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null) { lock (_lock) return _accounts[accountId].Fills.ToList(); }
    public IReadOnlyList<CashEntry> GetCashEntries(string accountId) => [];
    public IReadOnlyList<EquitySnapshot> GetEquityCurve(string accountId) { lock (_lock) return [new EquitySnapshot(clock.UtcNow, _accounts[accountId].Cash, 0, _accounts[accountId].Cash)]; }
    public PerformanceStats GetPerformance(string accountId, CommandSource? source = null, string? strategyTag = null) => new() { Source = source, StrategyTag = strategyTag };
    public RiskConfig GetRiskConfig(string accountId) { lock (_lock) return _accounts[accountId].Risk; }
    public FeeModelConfig GetFeeModel(string accountId, AssetClass assetClass) { lock (_lock) return assetClass == AssetClass.Crypto ? _accounts[accountId].CryptoFees : _accounts[accountId].StockFees; }
    public bool IsKillSwitchEngaged(string accountId) { lock (_lock) return _accounts.TryGetValue(accountId, out var a) && a.KillSwitch; }
}
