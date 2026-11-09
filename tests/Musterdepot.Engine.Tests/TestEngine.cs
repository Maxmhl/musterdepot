using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;
using Musterdepot.Core.Events;
using Musterdepot.Core.MarketData;
using Musterdepot.Engine.Calendar;
using Musterdepot.Engine.Instruments;
using Musterdepot.Engine.State;

namespace Musterdepot.Engine.Tests;

/// <summary>Testaufbau: feste Uhr, fortlaufende IDs, ein Konto "main".</summary>
internal sealed class TestEngine
{
    /// <summary>Dienstag, 10.11.2026, 11:00 Berlin (gettex geöffnet).</summary>
    public static readonly DateTimeOffset T0 = new(2026, 11, 10, 10, 0, 0, TimeSpan.Zero);
    public const string Acc = "main";
    public const string Btc = "BTC/EUR";
    public const string Sap = "DE0007164600";

    public static FeeModelConfig NoFees { get; } = new() { Kind = FeeModelKind.None, FixedFee = 0 };

    public static RiskConfig Permissive { get; } = new()
    {
        MaxOrderNotional = null,
        MaxOrderPercentOfEquity = null,
        MaxPositionNotional = null,
        MaxPositionPercentOfEquity = null,
        MaxDailyLoss = null,
        PriceBandPercent = 50m,
        MaxOrdersPerMinuteHuman = 10_000,
        MaxOrdersPerMinuteAi = 10_000,
        MaxOpenOrders = 10_000,
    };

    public ManualClock Clock { get; }
    public TradingEngine Engine { get; }
    public List<DomainEvent> Appended { get; } = [];

    public TestEngine(
        RiskConfig? risk = null,
        FeeModelConfig? cryptoFees = null,
        FeeModelConfig? stockFees = null,
        decimal initialCash = 100_000m,
        IFxRateProvider? fx = null,
        ExecutionConfig? execution = null,
        IInstrumentResolver? instruments = null,
        DateTimeOffset? start = null,
        bool openAccount = true)
    {
        Clock = new ManualClock(start ?? T0);
        Engine = new TradingEngine(Clock, new SequentialIdGenerator(), fx, new GettexTradingCalendar(), instruments, execution);
        Engine.EventsAppended += (_, e) => Appended.AddRange(e.Events);
        if (openAccount)
        {
            var r = Engine.Execute(new OpenAccountRequest
            {
                AccountId = Acc,
                Name = "Test",
                InitialCash = initialCash,
                Risk = risk ?? Permissive,
                CryptoFees = cryptoFees ?? NoFees,
                StockFees = stockFees ?? NoFees,
            });
            Assert.True(r.Accepted, r.RejectReason);
        }
    }

    public DateTimeOffset Now => Clock.UtcNow;
    public AccountState State => Engine.GetAccountState(Acc)!;
    public PortfolioSnapshot Portfolio => Engine.GetPortfolio(Acc);
    public IReadOnlyList<Fill> Fills => Engine.GetFills(Acc);

    public void Advance(double ms) => Clock.Advance(TimeSpan.FromMilliseconds(ms));
    public void AdvanceAndTick(TimeSpan d) { Clock.Advance(d); Engine.OnClockTick(Clock.UtcNow); }

    public void Trade(string symbol, decimal price, decimal qty = 10m, double asOfOffsetMs = 0, OrderSide? aggressor = null, int delayedMinutes = 0, DateTimeOffset? asOf = null)
        => Engine.OnMarketEvent(new TradeTick
        {
            Symbol = symbol,
            Source = delayedMinutes > 0 ? "gettex" : "kraken",
            AsOfUtc = asOf ?? Clock.UtcNow.AddMilliseconds(asOfOffsetMs),
            ReceivedUtc = Clock.UtcNow,
            Price = price,
            Quantity = qty,
            AggressorSide = aggressor,
            DelayedMinutes = delayedMinutes,
            Quality = delayedMinutes > 0 ? DataQuality.Delayed : DataQuality.Realtime,
        });

    public void Quote(string symbol, decimal bid, decimal ask, double asOfOffsetMs = 0)
        => Engine.OnMarketEvent(new QuoteTick
        {
            Symbol = symbol, Source = "kraken", AsOfUtc = Clock.UtcNow.AddMilliseconds(asOfOffsetMs), ReceivedUtc = Clock.UtcNow, Bid = bid, Ask = ask,
        });

    public void Book(string symbol, (decimal P, decimal Q)[] bids, (decimal P, decimal Q)[] asks, bool snapshot = true, double asOfOffsetMs = 0)
        => Engine.OnMarketEvent(new OrderBookUpdate
        {
            Symbol = symbol,
            Source = "kraken",
            AsOfUtc = Clock.UtcNow.AddMilliseconds(asOfOffsetMs),
            ReceivedUtc = Clock.UtcNow,
            IsSnapshot = snapshot,
            Bids = bids.Select(b => new OrderBookLevel(b.P, b.Q)).ToList(),
            Asks = asks.Select(a => new OrderBookLevel(a.P, a.Q)).ToList(),
        });

    private int _coid;

    public EngineResult Place(string symbol, OrderSide side, OrderType type, decimal? qty = null, decimal? limit = null, decimal? stop = null,
        TimeInForce tif = TimeInForce.Gtc, CommandSource source = CommandSource.Human, string? clientOrderId = null, string? tag = null,
        decimal? notional = null, decimal? tp = null, decimal? sl = null, string? oco = null, decimal? trailAmount = null, decimal? trailPercent = null,
        DateTimeOffset? expires = null)
        => Engine.Execute(Request(symbol, side, type, qty, limit, stop, tif, source, clientOrderId, tag, notional, tp, sl, oco, trailAmount, trailPercent, expires));

    public PlaceOrderRequest Request(string symbol, OrderSide side, OrderType type, decimal? qty = null, decimal? limit = null, decimal? stop = null,
        TimeInForce tif = TimeInForce.Gtc, CommandSource source = CommandSource.Human, string? clientOrderId = null, string? tag = null,
        decimal? notional = null, decimal? tp = null, decimal? sl = null, string? oco = null, decimal? trailAmount = null, decimal? trailPercent = null,
        DateTimeOffset? expires = null)
        => new()
        {
            AccountId = Acc,
            ClientOrderId = clientOrderId ?? $"c{++_coid}",
            Symbol = symbol,
            Side = side,
            Type = type,
            Quantity = notional is null ? qty : null,
            Notional = notional,
            LimitPrice = limit,
            StopPrice = stop,
            TimeInForce = tif,
            Source = source,
            StrategyTag = tag,
            TakeProfitPrice = tp,
            StopLossPrice = sl,
            OcoGroupId = oco,
            TrailAmount = trailAmount,
            TrailPercent = trailPercent,
            ExpiresUtc = expires,
        };

    public Order Order(string orderId) => Engine.GetOrder(Acc, orderId)!;

    /// <summary>Kauft eine Position per Market-Order (Trade als Kursquelle) und gibt die Order zurück.</summary>
    public Order Buy(string symbol, decimal qty, decimal price, CommandSource source = CommandSource.Human, string? tag = null)
    {
        Trade(symbol, price);
        var r = Place(symbol, OrderSide.Buy, OrderType.Market, qty, source: source, tag: tag);
        Assert.True(r.Accepted, r.RejectReason);
        Advance(250);
        Trade(symbol, price);
        var o = Order(r.OrderId!);
        Assert.Equal(OrderStatus.Filled, o.Status);
        return o;
    }

    public Order Sell(string symbol, decimal qty, decimal price, CommandSource source = CommandSource.Human)
    {
        Trade(symbol, price);
        var r = Place(symbol, OrderSide.Sell, OrderType.Market, qty, source: source);
        Assert.True(r.Accepted, r.RejectReason);
        Advance(250);
        Trade(symbol, price);
        var o = Order(r.OrderId!);
        Assert.Equal(OrderStatus.Filled, o.Status);
        return o;
    }

    public IEnumerable<T> Events<T>() where T : DomainEvent => Engine.GetEvents(Acc).OfType<T>();
}

internal sealed class FixedFx(decimal rate, string source = "test-fx") : IFxRateProvider
{
    public decimal? GetRate(string fromCurrency, string toCurrency) => fromCurrency == "USD" && toCurrency == "EUR" ? rate : null;
    public DateTimeOffset? AsOfUtc => TestEngine.T0;
    public string SourceDescription => source;
    public Task RefreshAsync(CancellationToken ct) => Task.CompletedTask;
}
