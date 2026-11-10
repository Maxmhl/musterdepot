using FsCheck.Xunit;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Commands;
using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;
using Musterdepot.Engine;
using Musterdepot.IntegrationTests.Infrastructure;

namespace Musterdepot.IntegrationTests;

/// <summary>
/// Eigenschaftstests über den vollständigen DI-Container: Befehle laufen durch <see cref="ICommandBus"/>
/// (Mensch und KI), Marktdaten in die Engine aus dem Container. FsCheck erzeugt und verkleinert die Aktionsfolgen.
/// </summary>
public class DiPropertyTests
{
    private const string Acc = "main";
    private const string Btc = "BTC/EUR";
    private const string AiClient = "prop-ai";

    public static readonly RiskConfig Risk = new()
    {
        MaxOrderNotional = 5_000m,
        MaxOrderPercentOfEquity = 50m,
        MaxPositionNotional = 12_000m,
        MaxPositionPercentOfEquity = 80m,
        MaxDailyLoss = 2_000m,
        MaxDailyLossPercent = 20m,
        PriceBandPercent = 5m,
        MaxOrdersPerMinuteHuman = 1000,
        MaxOrdersPerMinuteAi = 1000,
        MaxOpenOrders = 15,
        AllowedSymbols = [Btc],
    };

    private sealed class Sim : IAsyncDisposable
    {
        public readonly TestHost Host;
        public readonly Random R;
        public decimal Price = 50_000m;
        public readonly List<(DateTimeOffset T, decimal Price)> Trades = [];
        public int Executed, Rejected, Denied;
        public readonly List<string> Reasons = [];
        private int _keys;

        private Sim(TestHost host, int seed)
        {
            Host = host;
            R = new Random(seed);
        }

        public static Sim Create(int seed)
        {
            var host = TestHost.Create(o => { o.AddMcp = false; o.Seed = seed; });
            var sim = new Sim(host, seed);
            Run(() => host.UpdateSettingsAsync(s => s with
            {
                Risk = Risk,
                CryptoFees = FeeModelConfig.KrakenLike,
                AiClients = [new AiClientConfig { ClientId = AiClient, Name = "Prop-KI", Mode = ClientMode.Autonomous, Scopes = ClientScopes.Read | ClientScopes.Trade }],
            }));
            var open = host.Engine.Execute(new OpenAccountRequest { AccountId = Acc, Name = "Prop", InitialCash = 20_000m, Risk = Risk, CryptoFees = FeeModelConfig.KrakenLike });
            Assert.True(open.Accepted, open.RejectReason);
            sim.Market();
            return sim;
        }

        public ITradingEngine Engine => Host.Engine;
        public ManualClock Clock => Host.Clock;

        private static T Run<T>(Func<Task<T>> f) => Task.Run(f).GetAwaiter().GetResult();

        public CommandResult Send(ICommand c)
        {
            Clock.Advance(TimeSpan.FromMilliseconds(300));
            var r = Run(() => Host.Bus.SendAsync(c));
            if (r.Status == CommandStatus.Executed) Executed++;
            else if (r.Status == CommandStatus.Rejected) Rejected++;
            else if (r.Status == CommandStatus.Denied) Denied++;
            if (!r.Success) Reasons.Add(System.Text.RegularExpressions.Regex.Replace(r.Message ?? "", @"[0-9.,]+", "#"));
            return r;
        }

        public string Key() => $"k{++_keys}";
        private decimal Qty() => Math.Round(0.002m + (decimal)R.NextDouble() * 0.15m, 4);
        private OrderSide Side() => R.Next(5) < 3 ? OrderSide.Buy : OrderSide.Sell;
        private decimal Around(decimal pct) => Math.Round(Price * (1 + ((decimal)R.NextDouble() * 2 - 1) * pct / 100m), 1);
        private (CommandSource, string) Who() => R.Next(3) == 0 ? (CommandSource.Ai, AiClient) : (CommandSource.Human, "ui");

        /// <summary>Kurs bewegen: Buch-Snapshot und ein Trade.</summary>
        public void Market()
        {
            Clock.Advance(TimeSpan.FromMilliseconds(R.Next(200, 3000)));
            Price = Math.Max(1_000m, Math.Round(Price * (1 + ((decimal)R.NextDouble() * 2 - 1) * 0.008m), 1));
            var now = Clock.UtcNow;
            Engine.OnMarketEvent(new OrderBookUpdate
            {
                Symbol = Btc, Source = "kraken", AsOfUtc = now, ReceivedUtc = now, IsSnapshot = true,
                Bids = Enumerable.Range(0, 5).Select(i => new OrderBookLevel(Price - 0.1m - i * 5m, Qty())).ToList(),
                Asks = Enumerable.Range(0, 5).Select(i => new OrderBookLevel(Price + 0.1m + i * 5m, Qty())).ToList(),
            });
            Engine.OnMarketEvent(new TradeTick
            {
                Symbol = Btc, Source = "kraken", AsOfUtc = now, ReceivedUtc = now, Price = Price, Quantity = Qty() * 3,
                AggressorSide = Side(),
            });
            Trades.Add((now, Price));
            Engine.OnClockTick(now);
        }

        public void Step(int op)
        {
            var (src, client) = Who();
            switch (Math.Abs(op) % 10)
            {
                case 0 or 1 or 2:
                    Market();
                    break;
                case 3:
                    Send(new PlaceOrderCommand { Source = src, ClientId = client, IdempotencyKey = Key(), Symbol = Btc, Side = Side(), Type = OrderType.Market, Quantity = Qty() });
                    break;
                case 4:
                    Send(new PlaceOrderCommand { Source = src, ClientId = client, IdempotencyKey = Key(), Symbol = Btc, Side = Side(), Type = OrderType.Limit, Quantity = Qty(), LimitPrice = Around(8m), TimeInForce = TimeInForce.Gtc });
                    break;
                case 5:
                {
                    var side = Side();
                    var stop = Around(3m);
                    var stopLimit = R.Next(2) == 0;
                    Send(new PlaceOrderCommand
                    {
                        Source = src, ClientId = client, IdempotencyKey = Key(), Symbol = Btc, Side = side,
                        Type = stopLimit ? OrderType.StopLimit : OrderType.Stop, Quantity = Qty(), StopPrice = stop,
                        LimitPrice = stopLimit ? (side == OrderSide.Buy ? stop + 20m : stop - 20m) : null, TimeInForce = TimeInForce.Gtc,
                    });
                    break;
                }
                case 6:
                {
                    var open = Engine.GetOrders(Acc, openOnly: true);
                    if (open.Count > 0)
                        Send(new CancelOrderCommand { Source = src, ClientId = client, IdempotencyKey = Key(), OrderId = open[R.Next(open.Count)].OrderId });
                    break;
                }
                case 7:
                    if (Engine.IsKillSwitchEngaged(Acc))
                    {
                        // Die KI darf den Not-Aus nie lösen.
                        var ai = Send(new ReleaseKillSwitchCommand { Source = CommandSource.Ai, ClientId = AiClient, IdempotencyKey = Key() });
                        Assert.False(ai.Success, "KI konnte den Not-Aus lösen");
                        Assert.True(Engine.IsKillSwitchEngaged(Acc));
                        if (R.Next(5) > 0)
                        {
                            Assert.True(Send(new ReleaseKillSwitchCommand { IdempotencyKey = Key() }).Success, "Mensch konnte den Not-Aus nicht lösen");
                            Assert.False(Engine.IsKillSwitchEngaged(Acc));
                        }
                    }
                    else if (R.Next(4) == 0)
                        Send(new EngageKillSwitchCommand { Source = CommandSource.Ai, ClientId = AiClient, IdempotencyKey = Key(), Reason = "Eigenschaftstest" });
                    break;
                case 8:
                    // Bewusst zu groß oder weit außerhalb des Preisbands → muss abgelehnt werden.
                    var big = R.Next(2) == 0;
                    var r = Send(new PlaceOrderCommand
                    {
                        Source = src, ClientId = client, IdempotencyKey = Key(), Symbol = Btc, Side = OrderSide.Buy, Type = OrderType.Limit,
                        Quantity = big ? 0.5m : 0.01m, LimitPrice = big ? Price : Math.Round(Price * 0.7m, 1),
                    });
                    Assert.False(r.Success, $"Risiko-Gate umgangen: {r.Message}");
                    break;
                case 9:
                    Clock.Advance(TimeSpan.FromSeconds(R.Next(1, 600)));
                    Engine.OnClockTick(Clock.UtcNow);
                    break;
            }
        }

        public decimal? LastPriceAt(DateTimeOffset t)
        {
            decimal? last = null;
            foreach (var (time, price) in Trades) { if (time <= t) last = price; else break; }
            return last;
        }

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    [Property(MaxTest = 25)]
    public void Invariants_hold_through_command_bus(int seed, int[] ops)
    {
        var sim = Sim.Create(seed);
        try
        {
            foreach (var op in ops.Take(60).Concat(Enumerable.Range(0, 40).Select(i => seed * 17 + i * 13)))
            {
                var before = sim.Engine.GetOrders(Acc).Count;
                var (exec, rej, den) = (sim.Executed, sim.Rejected, sim.Denied);
                sim.Step(op);
                var after = sim.Engine.GetOrders(Acc);
                if (sim.Rejected + sim.Denied > rej + den && sim.Executed == exec)
                    Assert.Equal(before, after.Count); // abgelehnt → keine Order
                Invariants.CheckAccount(sim.Engine, Acc);
            }
            var events = sim.Engine.GetEvents(Acc);
            Invariants.CheckRiskGate(events, sim.LastPriceAt);

            // LoadFromEvents ≡ Live (über JSON wie in der Datenbank).
            var restored = new TradingEngine(new ManualClock(sim.Clock.UtcNow), new SequentialIdGenerator());
            restored.LoadFromEvents(events.Select(e => Core.Serialization.JsonDefaults.DeserializeEvent(Core.Serialization.JsonDefaults.SerializeEvent(e))));
            Assert.Equal(Invariants.StateFingerprint(sim.Engine), Invariants.StateFingerprint(restored));
        }
        finally
        {
            sim.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    [Fact]
    public async Task Random_sequences_exercise_bus_and_engine()
    {
        int executed = 0, rejected = 0, denied = 0, fills = 0, cancels = 0;
        for (int seed = 1; seed <= 6; seed++)
        {
            await using var sim = Sim.Create(seed);
            for (int i = 0; i < 120; i++)
            {
                sim.Step(sim.R.Next());
                Invariants.CheckAccount(sim.Engine, Acc);
            }
            Invariants.CheckRiskGate(sim.Engine.GetEvents(Acc), sim.LastPriceAt);
            executed += sim.Executed;
            rejected += sim.Rejected;
            denied += sim.Denied;
            fills += sim.Engine.GetFills(Acc).Count;
            cancels += sim.Engine.GetOrders(Acc).Count(o => o.Status == OrderStatus.Cancelled);
        }
        Assert.True(executed > 50 && rejected > 20 && denied > 0 && fills > 20 && cancels > 3,
            $"executed={executed} rejected={rejected} denied={denied} fills={fills} cancels={cancels}");
    }
}
