using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Commands;
using Musterdepot.Core.Domain;
using Musterdepot.IntegrationTests.Infrastructure;
using Musterdepot.Recorder.Fixtures;

namespace Musterdepot.IntegrationTests;

public class OrderScriptTests
{
    public static TheoryData<string> Names() => ReplayFixture.Names();

    [Theory, MemberData(nameof(Names))]
    public void Script_IsValidAgainstItsReplay(string name)
    {
        var f = ReplayFixture.Load(name);
        Assert.Equal(name, f.Script.Scenario);
        Assert.Empty(OrderScriptValidator.Validate(f.Script, f.Events));
        Assert.NotEmpty(f.Script.Steps);
        Assert.Contains(f.Script.Steps, s => s.Source == CommandSource.Human);
        Assert.Contains(f.Script.Steps, s => s.Source == CommandSource.Ai);
    }

    [Theory, MemberData(nameof(Names))]
    public void Script_RoundTripsThroughJson(string name)
    {
        var f = ReplayFixture.Load(name);
        var text = File.ReadAllText(f.ScriptPath).Replace("\r\n", "\n");
        Assert.Equal(text, OrderScriptJson.Serialize(f.Script));
    }

    [Theory, MemberData(nameof(Names))]
    public void Script_ConvertsToEngineRequestsAndCommands(string name)
    {
        var f = ReplayFixture.Load(name);
        var ids = new Dictionary<string, string>();
        for (int i = 0; i < f.Script.Steps.Count; i++)
        {
            var step = f.Script.Steps[i];
            if (step.PlaceOrder is { } p) ids[p.ClientOrderId] = $"ord_{i:D6}";
            var request = step.ToEngineRequest(ids.GetValueOrDefault);
            var command = step.ToCommand(ids.GetValueOrDefault, i);
            Assert.Equal(step.AccountId, request.AccountId);
            Assert.Equal(step.Source, request.Source);
            Assert.Equal(step.Source, command.Source);
            Assert.True(command.IsWrite || step.PlaceOrder?.DryRun == true);
            switch (step.Action)
            {
                case OrderScriptAction.PlaceOrder:
                    var req = Assert.IsType<PlaceOrderRequest>(request);
                    var cmd = Assert.IsType<PlaceOrderCommand>(command);
                    Assert.Equal(step.PlaceOrder!.ClientOrderId, req.ClientOrderId);
                    Assert.Equal(req.ClientOrderId, cmd.IdempotencyKey);
                    Assert.Equal((req.Symbol, req.Side, req.Type, req.Quantity, req.Notional, req.LimitPrice, req.StopPrice, req.TimeInForce),
                                 (cmd.Symbol, cmd.Side, cmd.Type, cmd.Quantity, cmd.Notional, cmd.LimitPrice, cmd.StopPrice, cmd.TimeInForce));
                    break;
                case OrderScriptAction.Cancel:
                    var cancel = Assert.IsType<CancelOrderRequest>(request);
                    Assert.Equal(ids[step.Cancel!.ClientOrderId!], cancel.OrderId);
                    Assert.Equal(cancel.OrderId, Assert.IsType<CancelOrderCommand>(command).OrderId);
                    break;
            }
        }
        foreach (var a in f.Script.Accounts)
        {
            var open = a.ToRequest();
            Assert.Equal(a.AccountId, open.AccountId);
            Assert.True(open.InitialCash > 0);
        }
    }

    [Fact]
    public void Validator_ReportsTypicalMistakes()
    {
        var at = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
        var script = new OrderScript
        {
            Scenario = "kaputt",
            Replay = "kaputt.jsonl",
            Instruments = [FixtureGenerator.BtcEur],
            Accounts = [new OrderScriptAccount { AccountId = "main", Name = "x", InitialCash = 1000m }],
            Steps =
            [
                new OrderScriptStep
                {
                    AtUtc = at.AddMinutes(5), Action = OrderScriptAction.PlaceOrder, AccountId = "main",
                    PlaceOrder = new OrderScriptPlaceOrder { ClientOrderId = "a", Symbol = "BTC/EUR", Side = OrderSide.Buy, Type = OrderType.Limit, Quantity = 1m },
                },
                new OrderScriptStep
                {
                    AtUtc = at, Action = OrderScriptAction.PlaceOrder, AccountId = "nirgendwo",
                    PlaceOrder = new OrderScriptPlaceOrder { ClientOrderId = "a", Symbol = "ETH/EUR", Side = OrderSide.Buy, Type = OrderType.Market, Quantity = 1m, Notional = 5m },
                },
                new OrderScriptStep
                {
                    AtUtc = at.AddMinutes(6), Action = OrderScriptAction.Cancel, AccountId = "main",
                    Cancel = new OrderScriptCancel { ClientOrderId = "gibt-es-nicht" },
                },
            ],
        };
        var errors = string.Join("\n", OrderScriptValidator.Validate(script));
        Assert.Contains("braucht limitPrice", errors);
        Assert.Contains("nicht zeitlich sortiert", errors);
        Assert.Contains("unbekanntes Konto", errors);
        Assert.Contains("clientOrderId 'a' doppelt", errors);
        Assert.Contains("quantity/notional", errors);
        Assert.Contains("ETH/EUR", errors);
        Assert.Contains("gibt-es-nicht", errors);
    }

    [Fact]
    public void Timeline_MergesMarketDataAndOrdersInTimeOrder()
    {
        var f = ReplayFixture.Load(FixtureGenerator.GapName);
        var timeline = f.Timeline().ToList();
        Assert.Equal(f.Events.Count + f.Script.Steps.Count, timeline.Count);
        for (int i = 1; i < timeline.Count; i++)
            Assert.True(timeline[i].AtUtc >= timeline[i - 1].AtUtc);
        var orderSteps = timeline.OfType<OrderReplayStep>().ToList();
        Assert.Equal(Enumerable.Range(0, f.Script.Steps.Count), orderSteps.Select(s => s.Index));
        // Bei gleichem Zeitpunkt: Marktdaten zuerst, d. h. vor jedem Orderschritt liegt kein späteres Ereignis.
        foreach (var o in orderSteps)
        {
            var pos = timeline.IndexOf(o);
            Assert.All(timeline.Take(pos).OfType<MarketReplayStep>(), m => Assert.True(m.AtUtc <= o.AtUtc));
        }
    }
}
