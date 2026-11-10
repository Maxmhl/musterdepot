using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;
using Musterdepot.Core.Events;
using Musterdepot.Core.MarketData;
using Musterdepot.Engine.Calendar;
using Musterdepot.Engine.Core;
using Musterdepot.Engine.Instruments;
using Musterdepot.Engine.Market;
using Musterdepot.Engine.State;
using Musterdepot.Engine.Stats;

namespace Musterdepot.Engine;

/// <summary>
/// Thread-sicherer Wrapper um <see cref="TradingCore"/> (ein Schreiber, Lock). Vergibt Sequence und EventId je Konto,
/// wendet Events per Evolve an und löst <see cref="EventsAppended"/> aus (innerhalb des Locks, damit die Reihenfolge stimmt;
/// Handler dürfen lesend auf die Engine zugreifen).
/// </summary>
public sealed class TradingEngine : ITradingEngine
{
    private readonly Lock _lock = new();
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;
    private readonly IFxRateProvider? _fx;
    private readonly ITradingCalendar _calendar;
    private readonly IInstrumentResolver _instruments;
    private readonly ExecutionConfig _execution;
    private readonly EngineOptions _options;
    private readonly Dictionary<string, AccountState> _accounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<DomainEvent>> _events = new(StringComparer.Ordinal);
    private readonly List<string> _accountOrder = [];
    private MarketState _market = MarketState.Empty;

    public TradingEngine(
        IClock clock,
        IIdGenerator ids,
        IFxRateProvider? fx = null,
        ITradingCalendar? calendar = null,
        IInstrumentResolver? instruments = null,
        ExecutionConfig? execution = null,
        EngineOptions? options = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _fx = fx;
        _calendar = calendar ?? new GettexTradingCalendar();
        _instruments = instruments ?? HeuristicInstrumentResolver.Instance;
        _execution = execution ?? new ExecutionConfig();
        _options = options ?? EngineOptions.Default;
    }

    public event EventHandler<EngineEventsEventArgs>? EventsAppended;

    public ExecutionConfig Execution => _execution;
    public ITradingCalendar Calendar => _calendar;
    public IInstrumentResolver Instruments => _instruments;

    /// <summary>Aktueller Marktzustand (letzte Kurse, gepflegte Orderbücher).</summary>
    public MarketState Market { get { lock (_lock) return _market; } }

    private EngineContext Context(DateTimeOffset now, IIdGenerator? ids = null) => new()
    {
        Now = now,
        Ids = ids ?? _ids,
        Instruments = _instruments,
        Calendar = _calendar,
        Fx = _fx,
        Execution = _execution,
        Options = _options,
    };

    // ------------------------------------------------------------------ Schreiben

    public EngineResult Execute(EngineCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_lock)
        {
            _accounts.TryGetValue(command.AccountId ?? "", out var state);
            var decision = TradingCore.Decide(state, command, _market, Context(_clock.UtcNow));
            var stamped = Commit(command.AccountId!, decision.Events);
            Raise(stamped);
            return new EngineResult
            {
                Accepted = decision.Accepted,
                Events = stamped,
                RejectReason = decision.RejectReason,
                RiskRule = decision.RiskRule,
                OrderId = decision.OrderId,
            };
        }
    }

    public OrderPreview Preview(PlaceOrderRequest request)
    {
        lock (_lock)
        {
            _accounts.TryGetValue(request.AccountId, out var state);
            return TradingCore.Preview(state, request, _market, Context(_clock.UtcNow, new SequentialIdGenerator()));
        }
    }

    public void OnMarketEvent(MarketEvent marketEvent)
    {
        ArgumentNullException.ThrowIfNull(marketEvent);
        lock (_lock)
        {
            _market = _market.Apply(marketEvent);
            var ctx = Context(_clock.UtcNow);
            var batches = new List<IReadOnlyList<DomainEvent>>();
            foreach (var id in _accountOrder)
            {
                var events = TradingCore.Decide(_accounts[id], marketEvent, _market, ctx);
                if (events.Count > 0) batches.Add(Commit(id, events));
            }
            foreach (var b in batches) Raise(b);
        }
    }

    public void OnClockTick(DateTimeOffset utcNow)
    {
        lock (_lock)
        {
            var ctx = Context(utcNow);
            var batches = new List<IReadOnlyList<DomainEvent>>();
            foreach (var id in _accountOrder)
            {
                var events = TradingCore.DecideClockTick(_accounts[id], _market, ctx);
                if (events.Count > 0) batches.Add(Commit(id, events));
            }
            foreach (var b in batches) Raise(b);
        }
    }

    public void LoadFromEvents(IEnumerable<DomainEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        lock (_lock)
        {
            _accounts.Clear();
            _events.Clear();
            _accountOrder.Clear();
            foreach (var group in events.GroupBy(e => e.AccountId, StringComparer.Ordinal))
            {
                AccountState? state = null;
                var list = new List<DomainEvent>();
                long seq = 0;
                foreach (var e in group.OrderBy(e => e.Sequence == 0 ? long.MaxValue : e.Sequence))
                {
                    var ev = e.Sequence == 0 ? e with { Sequence = seq + 1 } : e;
                    seq = Math.Max(seq, ev.Sequence);
                    state = TradingCore.Evolve(state, ev);
                    list.Add(ev);
                }
                if (state is null) continue;
                _accounts[group.Key] = state;
                _events[group.Key] = list;
                _accountOrder.Add(group.Key);
            }
        }
    }

    private IReadOnlyList<DomainEvent> Commit(string accountId, IReadOnlyList<DomainEvent> events)
    {
        if (events.Count == 0) return [];
        _accounts.TryGetValue(accountId, out var state);
        var seq = state?.LastSequence ?? 0;
        var stamped = new List<DomainEvent>(events.Count);
        foreach (var e in events)
        {
            var ev = e with { Sequence = ++seq, EventId = _ids.NewId("evt") };
            state = TradingCore.Evolve(state, ev);
            stamped.Add(ev);
        }
        if (!_accounts.ContainsKey(accountId)) _accountOrder.Add(accountId);
        _accounts[accountId] = state!;
        if (!_events.TryGetValue(accountId, out var list)) _events[accountId] = list = [];
        list.AddRange(stamped);
        return stamped;
    }

    private void Raise(IReadOnlyList<DomainEvent> events)
    {
        if (events.Count > 0) EventsAppended?.Invoke(this, new EngineEventsEventArgs(events));
    }

    // ------------------------------------------------------------------ Lesen

    /// <summary>Interner, unveränderlicher Kontozustand (z. B. für Tests und Diagnose).</summary>
    public AccountState? GetAccountState(string accountId)
    {
        lock (_lock) return _accounts.TryGetValue(accountId, out var s) ? s : null;
    }

    public IReadOnlyList<DomainEvent> GetEvents(string accountId, long afterSequence = 0)
    {
        lock (_lock)
            return _events.TryGetValue(accountId, out var list) ? list.Where(e => e.Sequence > afterSequence).ToList() : [];
    }

    public bool AccountExists(string accountId)
    {
        lock (_lock) return _accounts.ContainsKey(accountId);
    }

    public IReadOnlyList<Account> GetAccounts()
    {
        lock (_lock) return _accountOrder.Select(id => _accounts[id].Account).ToList();
    }

    public PortfolioSnapshot GetPortfolio(string accountId)
    {
        lock (_lock)
        {
            if (!_accounts.TryGetValue(accountId, out var s))
                return new PortfolioSnapshot { AccountId = accountId, AsOfUtc = _clock.UtcNow, Cash = 0, PositionsValue = 0, Positions = [] };
            return TradingCore.BuildPortfolio(s, _market, Context(_clock.UtcNow));
        }
    }

    public IReadOnlyList<Order> GetOrders(string accountId, bool openOnly = false)
    {
        lock (_lock)
        {
            if (!_accounts.TryGetValue(accountId, out var s)) return [];
            var all = s.OrderSequence.Select(id => s.Orders[id]);
            return (openOnly ? all.Where(o => o.IsOpen) : all).ToList();
        }
    }

    public Order? GetOrder(string accountId, string orderId)
    {
        lock (_lock) return _accounts.TryGetValue(accountId, out var s) && s.Orders.TryGetValue(orderId, out var o) ? o : null;
    }

    public Order? GetOrderByClientOrderId(string accountId, string clientOrderId)
    {
        lock (_lock) return _accounts.TryGetValue(accountId, out var s) ? s.FindByClientOrderId(clientOrderId) : null;
    }

    /// <summary>Abgelehnte Orders (aus <see cref="OrderRejected"/>-Events).</summary>
    public IReadOnlyList<OrderRejected> GetRejections(string accountId)
    {
        lock (_lock) return _accounts.TryGetValue(accountId, out var s) ? s.Rejections : [];
    }

    public IReadOnlyList<Fill> GetFills(string accountId, DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null)
    {
        lock (_lock)
        {
            if (!_accounts.TryGetValue(accountId, out var s)) return [];
            return s.Fills.Where(f => (fromUtc is null || f.TimestampUtc >= fromUtc) && (toUtc is null || f.TimestampUtc <= toUtc)).ToList();
        }
    }

    public IReadOnlyList<CashEntry> GetCashEntries(string accountId)
    {
        lock (_lock) return _accounts.TryGetValue(accountId, out var s) ? s.CashEntries : [];
    }

    public IReadOnlyList<EquitySnapshot> GetEquityCurve(string accountId)
    {
        lock (_lock) return _accounts.TryGetValue(accountId, out var s) ? s.EquityCurve : [];
    }

    public PerformanceStats GetPerformance(string accountId, CommandSource? source = null, string? strategyTag = null)
    {
        lock (_lock)
            return _accounts.TryGetValue(accountId, out var s)
                ? PerformanceCalculator.Compute(s, source, strategyTag)
                : new PerformanceStats { Source = source, StrategyTag = strategyTag };
    }

    public RiskConfig GetRiskConfig(string accountId)
    {
        lock (_lock) return _accounts.TryGetValue(accountId, out var s) ? s.Risk : RiskConfig.Default;
    }

    public FeeModelConfig GetFeeModel(string accountId, AssetClass assetClass)
    {
        lock (_lock)
            return _accounts.TryGetValue(accountId, out var s)
                ? s.FeeModel(assetClass)
                : assetClass == AssetClass.Crypto ? FeeModelConfig.KrakenLike : FeeModelConfig.NeoBroker;
    }

    public bool IsKillSwitchEngaged(string accountId)
    {
        lock (_lock) return _accounts.TryGetValue(accountId, out var s) && s.KillSwitchEngaged;
    }

    /// <summary>Zuletzt bekanntes (selbst gepflegtes) Orderbuch eines Symbols.</summary>
    public OrderBookSnapshot? GetOrderBook(string symbol, int depth = int.MaxValue)
    {
        lock (_lock) return _market.Get(symbol)?.ToOrderBookSnapshot(depth);
    }
}
