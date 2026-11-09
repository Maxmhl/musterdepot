using System.Text.Json;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;
using Musterdepot.Core.Events;
using Musterdepot.Core.MarketData;
using Musterdepot.Core.Serialization;
using Musterdepot.Engine.Calendar;
using Musterdepot.Engine.Instruments;

namespace Musterdepot.Engine.Backtest;

public sealed record StepResult(ScriptStep Step, EngineResult? Result);

public sealed record BacktestResult
{
    public required string AccountId { get; init; }
    public required IReadOnlyList<DomainEvent> Events { get; init; }
    public required PerformanceStats Stats { get; init; }
    public required PortfolioSnapshot Portfolio { get; init; }
    public required IReadOnlyList<Order> Orders { get; init; }
    public required IReadOnlyList<Fill> Fills { get; init; }
    public required IReadOnlyList<EquitySnapshot> EquityCurve { get; init; }
    public required IReadOnlyList<StepResult> StepResults { get; init; }
    public required TradingEngine Engine { get; init; }

    private static readonly JsonSerializerOptions Indented = new(JsonDefaults.Options) { WriteIndented = true };

    /// <summary>Event-Log als eingerücktes JSON-Array (Golden-Format).</summary>
    public string EventsToJson() => JsonSerializer.Serialize(Events, Indented);
}

/// <summary>
/// Replay/Backtest: speist eine Folge von Marktdaten mit <see cref="ManualClock"/> in eine frische Engine ein und führt
/// ein zeitgesteuertes Orderskript aus. Simulierte Uhrzeit eines Marktereignisses = <c>ReceivedUtc</c>, falls gesetzt,
/// sonst <c>AsOfUtc + DelayedMinutes</c> (verzögerte Daten kommen entsprechend später an). Skriptschritte laufen vor
/// Marktereignissen mit späterer Uhrzeit; vor jedem Schritt/Ereignis wird <see cref="ITradingEngine.OnClockTick"/> aufgerufen.
/// </summary>
public sealed class BacktestRunner
{
    private readonly IInstrumentResolver? _instruments;
    private readonly ITradingCalendar? _calendar;
    private readonly IFxRateProvider? _fx;
    private readonly ExecutionConfig? _execution;
    private readonly EngineOptions? _options;
    private readonly Func<IIdGenerator> _ids;

    public BacktestRunner(
        IInstrumentResolver? instruments = null,
        ITradingCalendar? calendar = null,
        IFxRateProvider? fx = null,
        ExecutionConfig? execution = null,
        EngineOptions? options = null,
        Func<IIdGenerator>? idGeneratorFactory = null)
    {
        _instruments = instruments;
        _calendar = calendar;
        _fx = fx;
        _execution = execution;
        _options = options;
        _ids = idGeneratorFactory ?? (() => new SequentialIdGenerator());
    }

    public static DateTimeOffset EventTime(MarketEvent e)
        => e.ReceivedUtc != default ? e.ReceivedUtc : e.AsOfUtc + TimeSpan.FromMinutes(e.DelayedMinutes);

    public BacktestResult Run(string marketJsonlPath, BacktestScript script)
        => Run(MarketEventJsonl.ReadAll(marketJsonlPath), script);

    public BacktestResult Run(IEnumerable<MarketEvent> marketEvents, BacktestScript script)
    {
        var events = marketEvents.ToList();
        var steps = script.Steps.Select((s, i) => (s, i)).OrderBy(x => x.s.AtUtc).ThenBy(x => x.i).Select(x => x.s).ToList();
        var start = script.StartUtc
            ?? new[] { events.Count > 0 ? events.Min(EventTime) : (DateTimeOffset?)null, steps.Count > 0 ? steps[0].AtUtc : null }
                .Where(t => t is not null).Select(t => t!.Value).DefaultIfEmpty(DateTimeOffset.UnixEpoch).Min();

        var clock = new ManualClock(start);
        var engine = new TradingEngine(clock, _ids(), _fx, _calendar ?? new GettexTradingCalendar(), _instruments, _execution, _options);
        var open = engine.Execute(new OpenAccountRequest
        {
            AccountId = script.AccountId,
            Name = script.Name,
            InitialCash = script.InitialCash,
            Risk = script.Risk,
            CryptoFees = script.CryptoFees,
            StockFees = script.StockFees,
        });
        if (!open.Accepted) throw new InvalidOperationException(open.RejectReason);

        var results = new List<StepResult>();
        int si = 0;

        void AdvanceTo(DateTimeOffset t)
        {
            if (t > clock.UtcNow) clock.Set(t);
            engine.OnClockTick(clock.UtcNow);
        }

        void RunStepsUntil(DateTimeOffset t)
        {
            while (si < steps.Count && steps[si].AtUtc <= t)
            {
                var step = steps[si++];
                AdvanceTo(step.AtUtc);
                var cmd = step.ToCommand(script.AccountId, engine);
                results.Add(new StepResult(step, cmd is null ? null : engine.Execute(cmd)));
            }
        }

        foreach (var e in events)
        {
            var t = EventTime(e);
            RunStepsUntil(t);
            AdvanceTo(t);
            engine.OnMarketEvent(e);
        }
        RunStepsUntil(DateTimeOffset.MaxValue);

        return new BacktestResult
        {
            AccountId = script.AccountId,
            Events = engine.GetEvents(script.AccountId),
            Stats = engine.GetPerformance(script.AccountId),
            Portfolio = engine.GetPortfolio(script.AccountId),
            Orders = engine.GetOrders(script.AccountId),
            Fills = engine.GetFills(script.AccountId),
            EquityCurve = engine.GetEquityCurve(script.AccountId),
            StepResults = results,
            Engine = engine,
        };
    }
}
