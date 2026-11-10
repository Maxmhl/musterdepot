using Musterdepot.Core.Domain;
using Musterdepot.Core.Events;
using Musterdepot.Engine.Backtest;
using Musterdepot.Engine.Instruments;
using Musterdepot.Recorder.Fixtures;

namespace Musterdepot.IntegrationTests.Infrastructure;

/// <summary>Ergebnis eines Schritts im Golden-Format.</summary>
public sealed record GoldenStepResult(int Step, DateTimeOffset AtUtc, OrderScriptAction Action, CommandSource Source,
    string? ClientOrderId, bool Accepted, string? RejectReason, string? RiskRule);

/// <summary>Inhalt von <c>tests/data/golden/replays/&lt;szenario&gt;.json</c>.</summary>
public sealed record GoldenReplayResult
{
    public required string Scenario { get; init; }
    public required IReadOnlyList<GoldenStepResult> Results { get; init; }
    public required IReadOnlyList<Order> Orders { get; init; }
    public required IReadOnlyList<Fill> Fills { get; init; }
    public required PortfolioSnapshot Portfolio { get; init; }
    public required PerformanceStats Stats { get; init; }
    public required IReadOnlyList<DomainEvent> Events { get; init; }
}

/// <summary>
/// Spielt ein Replay-Szenario (<see cref="ReplayFixture"/>) mit dem <see cref="BacktestRunner"/> der Engine ab
/// (frische <see cref="Musterdepot.Engine.TradingEngine"/>, ManualClock, fortlaufende IDs) und prüft die
/// <c>expect</c>-Felder des Orderskripts.
/// </summary>
public static class GoldenReplay
{
    public static BacktestScript ToBacktestScript(OrderScript script)
    {
        var account = script.Accounts.Single();
        return new BacktestScript
        {
            AccountId = account.AccountId,
            Name = account.Name,
            InitialCash = account.InitialCash,
            Risk = account.Risk,
            CryptoFees = account.CryptoFees,
            StockFees = account.StockFees,
            Steps = script.Steps.Select(ToStep).ToList(),
        };
    }

    private static ScriptStep ToStep(OrderScriptStep s) => s.Action switch
    {
        OrderScriptAction.PlaceOrder => new PlaceOrderStep
        {
            AtUtc = s.AtUtc, Source = s.Source, ClientName = s.ClientName,
            ClientOrderId = s.PlaceOrder!.ClientOrderId, Symbol = s.PlaceOrder.Symbol, Side = s.PlaceOrder.Side, Type = s.PlaceOrder.Type,
            Quantity = s.PlaceOrder.Quantity, Notional = s.PlaceOrder.Notional, LimitPrice = s.PlaceOrder.LimitPrice,
            StopPrice = s.PlaceOrder.StopPrice, TrailAmount = s.PlaceOrder.TrailAmount, TrailPercent = s.PlaceOrder.TrailPercent,
            TimeInForce = s.PlaceOrder.TimeInForce, ExpiresUtc = s.PlaceOrder.ExpiresUtc, StrategyTag = s.PlaceOrder.StrategyTag,
            TakeProfitPrice = s.PlaceOrder.TakeProfitPrice, StopLossPrice = s.PlaceOrder.StopLossPrice, OcoGroupId = s.PlaceOrder.OcoGroupId,
        },
        OrderScriptAction.Cancel => new CancelOrderStep
        {
            AtUtc = s.AtUtc, Source = s.Source, ClientName = s.ClientName,
            ClientOrderId = s.Cancel!.ClientOrderId ?? throw new NotSupportedException("Cancel per orderId im Replay nicht unterstützt."),
            Reason = s.Cancel.Reason,
        },
        OrderScriptAction.Modify => new ModifyOrderStep
        {
            AtUtc = s.AtUtc, Source = s.Source, ClientName = s.ClientName,
            ClientOrderId = s.Modify!.ClientOrderId ?? throw new NotSupportedException("Modify per orderId im Replay nicht unterstützt."),
            NewQuantity = s.Modify.NewQuantity, NewLimitPrice = s.Modify.NewLimitPrice, NewStopPrice = s.Modify.NewStopPrice,
        },
        _ => throw new NotSupportedException(s.Action.ToString()),
    };

    public static (GoldenReplayResult Golden, BacktestResult Raw) Run(ReplayFixture fixture)
    {
        var runner = new BacktestRunner(instruments: new StaticInstrumentResolver(fixture.Script.Instruments, HeuristicInstrumentResolver.Instance));
        var raw = runner.Run(fixture.Events, ToBacktestScript(fixture.Script));
        return (ToGolden(fixture, raw), raw);
    }

    public static GoldenReplayResult ToGolden(ReplayFixture fixture, BacktestResult raw) => new()
    {
        Scenario = fixture.Name,
        Results = fixture.Script.Steps.Select((s, i) =>
        {
            var r = raw.StepResults[i].Result;
            return new GoldenStepResult(i, s.AtUtc, s.Action, s.Source, TargetClientOrderId(s),
                r?.Accepted ?? false, r is null ? "Ziel-Order unbekannt" : r.RejectReason, r?.RiskRule);
        }).ToList(),
        Orders = raw.Orders,
        Fills = raw.Fills,
        Portfolio = raw.Portfolio,
        Stats = raw.Stats,
        Events = raw.Events,
    };

    public static string? TargetClientOrderId(OrderScriptStep s) =>
        s.PlaceOrder?.ClientOrderId ?? s.Cancel?.ClientOrderId ?? s.Modify?.ClientOrderId;

    /// <summary>Prüft alle <c>expect</c>-Felder; liefert die Abweichungen (leer = alles erfüllt).</summary>
    public static IReadOnlyList<string> CheckExpectations(OrderScript script, IReadOnlyList<GoldenStepResult> results,
        IReadOnlyList<Order> orders, IReadOnlyList<Fill> fills)
    {
        var problems = new List<string>();
        for (int i = 0; i < script.Steps.Count; i++)
        {
            var step = script.Steps[i];
            if (step.Expect is not { } x) continue;
            var target = TargetClientOrderId(step);
            var where = $"Schritt {i + 1} ({target})";
            if (x.Accepted is { } acc && results[i].Accepted != acc)
                problems.Add($"{where}: accepted={results[i].Accepted} erwartet {acc} ({results[i].RejectReason})");
            var order = orders.FirstOrDefault(o => o.ClientOrderId == target);
            var orderFills = order is null ? [] : fills.Where(f => f.OrderId == order.OrderId).ToList();
            if (x.FinalStatus is { } st && order?.Status != st)
                problems.Add($"{where}: Status {order?.Status.ToString() ?? "–"} erwartet {st}");
            if (x.ExecutedDuringGap is { } gap)
            {
                if (gap && orderFills.Count == 0) problems.Add($"{where}: kein Fill, erwartet Fill während der Lücke");
                foreach (var f in orderFills.Where(f => f.ExecutedDuringGap != gap))
                    problems.Add($"{where}: Fill {f.FillId} ExecutedDuringGap={f.ExecutedDuringGap} erwartet {gap}");
            }
            foreach (var f in orderFills)
            {
                if (x.MinFillPrice is { } min && f.Price < min) problems.Add($"{where}: Fill-Preis {f.Price} < {min}");
                if (x.MaxFillPrice is { } max && f.Price > max) problems.Add($"{where}: Fill-Preis {f.Price} > {max}");
                if (x.FillQuality is { } q && f.Quality != q) problems.Add($"{where}: Fill-Qualität {f.Quality} erwartet {q}");
                if (x.FillsOnlyFromMarketDataAfterUtc is { } after && f.MarketDataAsOfUtc <= after)
                    problems.Add($"{where}: Fill gegen Daten von {f.MarketDataAsOfUtc:O}, nicht nach {after:O}");
            }
        }
        return problems;
    }
}
