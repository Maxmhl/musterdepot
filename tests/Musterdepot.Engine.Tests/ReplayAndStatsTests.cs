using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;
using Musterdepot.Core.Events;
using Musterdepot.Core.Serialization;
using Musterdepot.Engine.Calendar;
using Musterdepot.Engine.Core;
using Musterdepot.Engine.Instruments;
using Musterdepot.Engine.State;
using static Musterdepot.Engine.Tests.TestEngine;

namespace Musterdepot.Engine.Tests;

public class ReplayTests
{
    internal static string Fingerprint(AccountState s) => JsonSerializer.Serialize(s, JsonDefaults.Options);

    internal static TestEngine RunComplexScenario()
    {
        // Hinweis: alle Risiko-Felder gesetzt – null-Werte überleben JsonDefaults (WhenWritingNull + Default ≠ null) nicht.
        var risk = new RiskConfig { MaxOrderNotional = 1_000_000m, MaxOrderPercentOfEquity = 100m, MaxPositionNotional = 1_000_000m, MaxPositionPercentOfEquity = 100m, MaxDailyLoss = 50_000m, MaxDailyLossPercent = 50m, PriceBandPercent = 50m };
        var t = new TestEngine(risk: risk, cryptoFees: FeeModelConfig.KrakenLike, stockFees: FeeModelConfig.NeoBroker);
        t.Book(Btc, [(99m, 2m), (98m, 5m)], [(100m, 1m), (101m, 5m)]);
        t.Place(Btc, OrderSide.Buy, OrderType.Market, 2m, tag: "s1");
        t.Advance(200);
        t.Trade(Btc, 100.5m);
        t.Place(Btc, OrderSide.Sell, OrderType.TrailingStop, 1m, trailPercent: 3m);
        t.Place(Btc, OrderSide.Buy, OrderType.Limit, 1m, limit: 97m, tp: 105m, sl: 92m, source: CommandSource.Ai, tag: "ki");
        t.Engine.Execute(new DepositCashRequest { AccountId = Acc, Amount = 1000m });
        for (int i = 0; i < 30; i++)
        {
            t.Advance(20_000);
            t.Trade(Btc, 100m + (decimal)Math.Sin(i / 3.0) * 6m, qty: 5m);
        }
        t.Trade(Sap, 200m, delayedMinutes: 15, asOf: t.Now.AddMinutes(-15));
        t.Place(Sap, OrderSide.Buy, OrderType.Market, 3m);
        t.Advance(1000);
        t.Trade(Sap, 201m, delayedMinutes: 15, asOf: t.Now);
        t.Engine.Execute(new EngageKillSwitchRequest { AccountId = Acc, Reason = "Test", Source = CommandSource.Ai });
        t.Engine.Execute(new ReleaseKillSwitchRequest { AccountId = Acc, ReleasedBy = "me" });
        t.Clock.Set(new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero));
        t.Engine.OnClockTick(t.Now);
        t.Trade(Btc, 101m);
        t.Sell(Btc, 0.5m, 101m);
        return t;
    }

    [Fact]
    public void Load_from_events_reproduces_live_state_exactly()
    {
        var t = RunComplexScenario();
        var events = t.Engine.GetEvents(Acc);
        Assert.True(events.Count > 20);
        Assert.Equal(Enumerable.Range(1, events.Count).Select(i => (long)i), events.Select(e => e.Sequence));
        Assert.Equal(events.Count, events.Select(e => e.EventId).Distinct().Count());
        Assert.Equal(events.Count, t.Appended.Count);

        // Über JSON (wie in der Datenbank) und zurück
        var roundTripped = events.Select(e => JsonDefaults.DeserializeEvent(JsonDefaults.SerializeEvent(e))).ToList();
        var restored = new TradingEngine(new ManualClock(t.Now), new SequentialIdGenerator());
        restored.LoadFromEvents(roundTripped);
        Assert.Equal(Fingerprint(t.State), Fingerprint(restored.GetAccountState(Acc)!));
        Assert.Equal(JsonSerializer.Serialize(t.Engine.GetPerformance(Acc), JsonDefaults.Options), JsonSerializer.Serialize(restored.GetPerformance(Acc), JsonDefaults.Options));

        // Reine Funktion: Replay ≡ Live
        Assert.Equal(Fingerprint(t.State), Fingerprint(TradingCore.Replay(events)!));
    }

    [Fact]
    public void Restored_engine_continues_sequence()
    {
        var t = RunComplexScenario();
        var restored = new TradingEngine(t.Clock, new SequentialIdGenerator());
        restored.LoadFromEvents(t.Engine.GetEvents(Acc));
        var r = restored.Execute(new DepositCashRequest { AccountId = Acc, Amount = 1m });
        Assert.Equal(t.Engine.GetEvents(Acc).Count + 1, r.Events.Single().Sequence);
    }
}

public class StatsTests
{
    [Fact]
    public void Performance_by_source_and_strategy()
    {
        var t = new TestEngine();
        t.Buy(Btc, 1m, 100m, tag: "h");
        t.Advance(60_000);
        t.Sell(Btc, 1m, 110m);                                   // +10, 60,25 s
        t.Buy(Btc, 1m, 100m, CommandSource.Ai, "ki1");
        t.Advance(120_000);
        t.Sell(Btc, 1m, 95m, CommandSource.Human);               // −5 (KI-Eröffnung)
        t.Buy(Btc, 1m, 100m, tag: "h");
        t.Sell(Btc, 1m, 120m);                                   // +20

        var all = t.Engine.GetPerformance(Acc);
        Assert.Equal(3, all.TradeCount);
        Assert.Equal(2, all.Wins);
        Assert.Equal(1, all.Losses);
        Assert.Equal(66.67m, all.WinRate);
        Assert.Equal(6m, all.ProfitFactor);
        Assert.Equal(8.33m, all.Expectancy);
        Assert.Equal(30m, all.GrossProfit);
        Assert.Equal(-5m, all.GrossLoss);
        Assert.Equal(25m, all.NetPnl);
        Assert.Equal(15m, all.AverageWin);
        Assert.Equal(-5m, all.AverageLoss);
        Assert.True(all.AverageHoldingTime > TimeSpan.FromSeconds(60));
        Assert.True(all.MaxDrawdown >= 5m);

        var ai = t.Engine.GetPerformance(Acc, CommandSource.Ai);
        Assert.Equal(1, ai.TradeCount);
        Assert.Equal(0m, ai.WinRate);
        Assert.Equal(0m, ai.ProfitFactor);
        Assert.Equal(5m, ai.MaxDrawdown);
        Assert.Equal(CommandSource.Ai, ai.Source);
        Assert.Equal(1, t.Engine.GetPerformance(Acc, strategyTag: "ki1").TradeCount);
        Assert.Equal(2, t.Engine.GetPerformance(Acc, CommandSource.Human, "h").TradeCount);
    }

    [Fact]
    public void Fee_share_and_fees()
    {
        var t = new TestEngine(cryptoFees: new FeeModelConfig { Kind = FeeModelKind.Fixed, FixedFee = 1m });
        t.Buy(Btc, 1m, 100m);
        t.Sell(Btc, 1m, 110m);
        var s = t.Engine.GetPerformance(Acc);
        Assert.Equal(8m, s.NetPnl);
        Assert.Equal(2m, s.TotalFees);
        Assert.Equal(20m, s.FeeSharePercent);
    }

    [Fact]
    public void Equity_curve_after_fills_and_at_most_once_per_minute_on_price_changes()
    {
        var t = new TestEngine();
        t.Buy(Btc, 1m, 100m);
        var before = t.Engine.GetEquityCurve(Acc).Count;
        for (int i = 0; i < 30; i++)
        {
            t.Advance(10_000);
            t.Trade(Btc, 100m + i);
        }
        var marks = t.Events<MarketPriceMarked>().ToList();
        Assert.InRange(marks.Count, 4, 6); // 5 Minuten → höchstens 1×/Minute
        Assert.All(marks.Zip(marks.Skip(1)), p => Assert.True(p.Second.TimestampUtc - p.First.TimestampUtc >= TimeSpan.FromMinutes(1)));
        var curve = t.Engine.GetEquityCurve(Acc);
        Assert.Equal(before + marks.Count, curve.Count);
        Assert.All(curve, p => Assert.Equal(p.Cash + p.PositionsValue, p.Equity));
        var pf = t.Portfolio;
        Assert.Equal(pf.Cash + pf.PositionsValue, pf.Equity);
        Assert.Equal(129m, pf.Positions.Single().LastPrice);
    }
}

public class CalendarTests
{
    private static readonly Instrument Stock = HeuristicInstrumentResolver.Instance.Resolve(Sap)!;
    private static readonly Instrument Crypto = HeuristicInstrumentResolver.Instance.Resolve(Btc)!;
    private static DateTimeOffset Berlin(int y, int m, int d, int h, int min) => BerlinTime.ToUtc(new DateOnly(y, m, d), new TimeOnly(h, min));

    [Fact]
    public void Easter_and_holidays()
    {
        Assert.Equal(new DateOnly(2026, 4, 5), GettexTradingCalendar.EasterSunday(2026));
        Assert.Equal(new DateOnly(2027, 3, 28), GettexTradingCalendar.EasterSunday(2027));
        var cal = new GettexTradingCalendar();
        Assert.False(cal.IsTradingDay(new DateOnly(2026, 4, 3)));  // Karfreitag
        Assert.False(cal.IsTradingDay(new DateOnly(2026, 4, 6)));  // Ostermontag
        Assert.False(cal.IsTradingDay(new DateOnly(2026, 5, 1)));
        Assert.False(cal.IsTradingDay(new DateOnly(2026, 12, 24)));
        Assert.False(cal.IsTradingDay(new DateOnly(2026, 12, 31)));
        Assert.False(cal.IsTradingDay(new DateOnly(2027, 1, 1)));
        Assert.False(cal.IsTradingDay(new DateOnly(2026, 11, 14))); // Samstag
        Assert.True(cal.IsTradingDay(new DateOnly(2026, 11, 10)));
    }

    [Fact]
    public void Session_hours_berlin_including_dst()
    {
        var cal = new GettexTradingCalendar();
        Assert.False(cal.IsOpen(Stock, Berlin(2026, 11, 10, 7, 29)));
        Assert.True(cal.IsOpen(Stock, Berlin(2026, 11, 10, 7, 30)));
        Assert.True(cal.IsOpen(Stock, Berlin(2026, 11, 10, 22, 59)));
        Assert.False(cal.IsOpen(Stock, Berlin(2026, 11, 10, 23, 0)));
        Assert.True(cal.IsOpen(Stock, new DateTimeOffset(2026, 7, 1, 5, 30, 0, TimeSpan.Zero))); // 07:30 MESZ
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 21, 0, 0, TimeSpan.Zero), cal.SessionCloseUtc(Stock, new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero)));
        // Freitagabend → nächste Öffnung Montag
        Assert.Equal(Berlin(2026, 11, 16, 7, 30), cal.NextOpenUtc(Stock, Berlin(2026, 11, 13, 23, 30)));
        Assert.True(cal.IsOpen(Crypto, Berlin(2026, 12, 25, 3, 0)));
        Assert.Null(cal.SessionCloseUtc(Crypto, Berlin(2026, 12, 25, 3, 0)));
    }

    [Fact]
    public void Configurable_hours()
    {
        var cal = new GettexTradingCalendar(new GettexCalendarOptions { OpenTime = new(9, 0), CloseTime = new(17, 30), Holidays = ExchangeHoliday.None });
        Assert.False(cal.IsOpen(Stock, Berlin(2026, 11, 10, 8, 0)));
        Assert.True(cal.IsTradingDay(new DateOnly(2026, 12, 24)));
    }
}

public class DependencyInjectionTests
{
    [Fact]
    public void AddMusterdepotEngine_registers_engine_and_defaults()
    {
        var services = new ServiceCollection();
        var clock = new ManualClock(T0);
        services.AddSingleton<IClock>(clock);
        services.AddMusterdepotEngine();
        using var sp = services.BuildServiceProvider();
        var engine = sp.GetRequiredService<ITradingEngine>();
        Assert.IsType<TradingEngine>(engine);
        Assert.Same(engine, sp.GetRequiredService<ITradingEngine>());
        Assert.IsType<GettexTradingCalendar>(sp.GetRequiredService<Musterdepot.Core.MarketData.ITradingCalendar>());
        Assert.NotNull(sp.GetRequiredService<IInstrumentResolver>().Resolve(Btc));
        Assert.IsType<GuidIdGenerator>(sp.GetRequiredService<IIdGenerator>());
        Assert.NotNull(sp.GetRequiredService<IRandomSource>());
        Assert.NotNull(sp.GetRequiredService<ExecutionConfig>());
        Assert.True(engine.Execute(new OpenAccountRequest { AccountId = "main", Name = "x", InitialCash = 1000m }).Accepted);
        Assert.Equal(T0, engine.GetAccounts().Single().CreatedUtc);
    }
}
