using Microsoft.Extensions.Logging;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;
using Musterdepot.MarketData.Gettex;
using Musterdepot.MarketData.Infrastructure;
using Musterdepot.MarketData.Sources;

namespace Musterdepot.MarketData.Fake;

/// <summary>Ein simuliertes Instrument.</summary>
public sealed record FakeInstrumentSpec
{
    public required Instrument Instrument { get; init; }
    public required decimal StartPrice { get; init; }
    /// <summary>Tagesvolatilität (Standardabweichung der Tagesrendite), z. B. 0.035 = 3,5 %.</summary>
    public double DailyVolatility { get; init; } = 0.02;
    public decimal TypicalTradeSize { get; init; } = 1m;
    /// <summary>Spread als Anteil des Preises (0.0002 = 2 Basispunkte).</summary>
    public decimal SpreadFraction { get; init; } = 0.0005m;
    public bool IsDelayed => Instrument.AssetClass is AssetClass.Stock or AssetClass.Etf;
}

public sealed record FakeMarketDataOptions
{
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromMilliseconds(500);
    public IReadOnlyList<FakeInstrumentSpec> Instruments { get; init; } = FakeMarketDataSource.DefaultInstruments();
    /// <summary>Alle Instrumente ohne Abo simulieren (Phase 1: Oberfläche zeigt sofort Kurse).</summary>
    public bool AutoSubscribeAll { get; init; } = true;
    public int BookDepth { get; init; } = 20;
    public int StockDelayMinutes { get; init; } = 15;
    /// <summary>Alle n Schritte ein vollständiger Orderbuch-Snapshot, dazwischen Deltas.</summary>
    public int BookSnapshotEvery { get; init; } = 50;
    public int MaxHistoryCandles { get; init; } = 5000;
    public int MaxTradesPerStep { get; init; } = 3;
}

/// <summary>
/// Simulierte Marktdaten ohne Netz (Phase 1): Random-Walk mit <see cref="IRandomSource"/> für Krypto-Paare und
/// einige Aktien (verzögert markiert). Erzeugt Trades, Kurse, Orderbuch (Snapshot + Deltas), Kerzen und eine
/// reproduzierbare Historie für <see cref="GetCandlesAsync"/>.
/// </summary>
public sealed class FakeMarketDataSource : MarketDataSourceBase, IHistoryCapabilities
{
    public const string SourceName = "fake";

    private sealed class SymbolState
    {
        public required FakeInstrumentSpec Spec;
        public decimal Price;
        public decimal SessionOpen;
        public decimal High, Low, Volume;
        public decimal? LastTrade;
        public IReadOnlyList<OrderBookLevel> Bids = [];
        public IReadOnlyList<OrderBookLevel> Asks = [];
        public long Steps;
        public long TradeSeq;
    }

    private readonly FakeMarketDataOptions _options;
    private readonly IRandomSource _random;
    private readonly Dictionary<string, SymbolState> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Candle>> _liveM1 = new(StringComparer.Ordinal);
    private readonly Dictionary<(string, Timeframe), List<Candle>> _historyCache = new();
    private readonly TradeCandleAggregator _m1 = new([Timeframe.M1], TimeSpan.Zero);
    private readonly Lock _lock = new();
    private readonly DateTimeOffset _sessionStartUtc;
    private CancellationTokenSource? _runCts;
    private Task? _runTask;

    public FakeMarketDataSource(IClock clock, IRandomSource random, FakeMarketDataOptions? options = null, ILogger<FakeMarketDataSource>? logger = null)
        : base(SourceName, clock, logger)
    {
        _options = options ?? new FakeMarketDataOptions();
        _random = random;
        _sessionStartUtc = clock.UtcNow;
        foreach (var spec in _options.Instruments)
            _states[spec.Instrument.Symbol] = new SymbolState
            {
                Spec = spec, Price = spec.StartPrice, SessionOpen = spec.StartPrice, High = spec.StartPrice, Low = spec.StartPrice,
            };
    }

    public static IReadOnlyList<FakeInstrumentSpec> DefaultInstruments()
    {
        static Instrument Crypto(string symbol, decimal tick, decimal step) => SymbolHelper.SynthesizeCryptoInstrument(symbol, SourceName) with { TickSize = tick, QuantityStep = step };
        static Instrument Stock(string isin) => GettexInstruments.ForIsin(isin) with { PrimarySource = SourceName };
        return
        [
            new() { Instrument = Crypto("BTC/EUR", 0.1m, 0.00000001m), StartPrice = 62_000m, DailyVolatility = 0.035, TypicalTradeSize = 0.02m, SpreadFraction = 0.0001m },
            new() { Instrument = Crypto("ETH/EUR", 0.01m, 0.00000001m), StartPrice = 3_100m, DailyVolatility = 0.045, TypicalTradeSize = 0.3m, SpreadFraction = 0.0002m },
            new() { Instrument = Crypto("SOL/EUR", 0.01m, 0.00000001m), StartPrice = 145m, DailyVolatility = 0.06, TypicalTradeSize = 5m, SpreadFraction = 0.0004m },
            new() { Instrument = Crypto("USDC/EUR", 0.0001m, 0.01m), StartPrice = 0.9250m, DailyVolatility = 0.003, TypicalTradeSize = 2_000m, SpreadFraction = 0.0001m },
            new() { Instrument = Stock("DE0007164600"), StartPrice = 182.40m, DailyVolatility = 0.015, TypicalTradeSize = 40m, SpreadFraction = 0.0008m }, // SAP
            new() { Instrument = Stock("DE0007236101"), StartPrice = 178.90m, DailyVolatility = 0.016, TypicalTradeSize = 40m, SpreadFraction = 0.0008m }, // Siemens
            new() { Instrument = Stock("DE0005557508"), StartPrice = 28.15m, DailyVolatility = 0.012, TypicalTradeSize = 300m, SpreadFraction = 0.001m }, // Telekom
            new() { Instrument = Stock("DE0008404005"), StartPrice = 281.30m, DailyVolatility = 0.013, TypicalTradeSize = 20m, SpreadFraction = 0.0008m }, // Allianz
            new() { Instrument = Stock("DE000BASF111"), StartPrice = 45.62m, DailyVolatility = 0.017, TypicalTradeSize = 150m, SpreadFraction = 0.001m }, // BASF
        ];
    }

    public override IReadOnlyCollection<AssetClass> AssetClasses { get; } = [AssetClass.Crypto, AssetClass.Stock];
    public override bool IsRealtime => true;
    protected override DataQuality DefaultQuality => DataQuality.Realtime;
    public TimeSpan? GetMaxHistoryLookback(Timeframe timeframe) => null;
    public Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } = Task.Delay;

    // ---------- Lebenszyklus ----------
    public override Task StartAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (_runTask is { IsCompleted: false }) return Task.CompletedTask;
            _runCts = new CancellationTokenSource();
            var token = _runCts.Token;
            _runTask = Task.Run(() => RunAsync(token), CancellationToken.None);
        }
        SetState(ConnectionState.Connected, "Simulierte Daten");
        return Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await DelayAsync(_options.TickInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            try { Step(); }
            catch (Exception ex) { Logger.LogError(ex, "Fake-Daten: Fehler im Schritt"); }
        }
    }

    private async Task StopLoopAsync()
    {
        Task? task;
        CancellationTokenSource? cts;
        lock (_lock) { task = _runTask; cts = _runCts; _runTask = null; _runCts = null; }
        if (cts is null) return;
        cts.Cancel();
        if (task is not null) { try { await task.ConfigureAwait(false); } catch { /* beendet */ } }
        cts.Dispose();
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        await StopLoopAsync().ConfigureAwait(false);
        SetState(ConnectionState.Disconnected, "Gestoppt");
    }

    protected override Task DisconnectForSuspendAsync(CancellationToken ct) => StopLoopAsync();

    protected override Task ReconnectAfterSuspendAsync(CancellationToken ct) => StartAsync(ct);

    // ---------- Simulation ----------
    private double Gaussian()
    {
        // Box-Muller
        var u1 = 1.0 - _random.NextDouble();
        var u2 = _random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private static decimal RoundToStep(decimal value, decimal step)
        => step <= 0 ? value : Math.Round(value / step, MidpointRounding.AwayFromZero) * step;

    private IEnumerable<SymbolState> ActiveStates()
    {
        var subs = Subscriptions;
        return _states.Values.Where(s => _options.AutoSubscribeAll || subs.Contains(s.Spec.Instrument.Symbol));
    }

    /// <summary>Ein Simulationsschritt für alle aktiven Instrumente (öffentlich für deterministische Tests).</summary>
    public IReadOnlyList<MarketEvent> Step()
    {
        var events = new List<MarketEvent>();
        var now = Clock.UtcNow;
        lock (_lock)
        {
            foreach (var st in ActiveStates()) StepSymbol(st, now, events);
        }
        PublishAll(events);
        return events;
    }

    private void StepSymbol(SymbolState st, DateTimeOffset now, List<MarketEvent> events)
    {
        var spec = st.Spec;
        var inst = spec.Instrument;
        var tick = inst.TickSize > 0 ? inst.TickSize : 0.01m;
        var delayed = spec.IsDelayed ? _options.StockDelayMinutes : 0;
        var quality = spec.IsDelayed ? DataQuality.Delayed : DataQuality.Realtime;
        var asOf = now - TimeSpan.FromMinutes(delayed);
        st.Steps++;

        // Random Walk (geometrisch), Volatilität auf die Schrittlänge skaliert.
        var sigma = spec.DailyVolatility * Math.Sqrt(_options.TickInterval.TotalSeconds / 86_400.0);
        var factor = 1m + (decimal)(sigma * Gaussian());
        st.Price = Math.Max(tick, RoundToStep(st.Price * factor, tick));

        var halfSpread = Math.Max(tick, RoundToStep(st.Price * spec.SpreadFraction / 2m, tick));
        var bid = st.Price - halfSpread;
        var ask = st.Price + halfSpread;
        if (bid <= 0) bid = tick;

        // Trades
        int n = (int)Math.Floor(_random.NextDouble() * (_options.MaxTradesPerStep + 1));
        for (int i = 0; i < n; i++)
        {
            bool buy = _random.NextDouble() < 0.5;
            var qty = RoundToStep(spec.TypicalTradeSize * (decimal)(0.1 + _random.NextDouble() * 1.9), inst.QuantityStep);
            if (qty <= 0) qty = inst.QuantityStep > 0 ? inst.QuantityStep : 1m;
            var px = buy ? ask : bid;
            st.LastTrade = px;
            st.Volume += qty;
            if (px > st.High) st.High = px;
            if (px < st.Low) st.Low = px;
            var tradeTime = asOf.AddTicks(i);
            events.Add(new TradeTick
            {
                Symbol = inst.Symbol, Source = SourceName, AsOfUtc = tradeTime, ReceivedUtc = now, DelayedMinutes = delayed, Quality = quality,
                Price = px, Quantity = qty, AggressorSide = buy ? OrderSide.Buy : OrderSide.Sell, TradeId = $"F{inst.Symbol}-{++st.TradeSeq}",
            });
            foreach (var c in _m1.AddTrade(inst.Symbol, SourceName, tradeTime, px, qty, now, quality, delayed))
                if (c.Candle.IsClosed)
                {
                    if (!_liveM1.TryGetValue(inst.Symbol, out var list)) _liveM1[inst.Symbol] = list = [];
                    list.Add(c.Candle);
                }
        }

        // Kurs und Buch nach den Trades dieses Schritts (AsOf je Symbol nicht fallend).
        var afterTrades = asOf.AddTicks(n);

        // Kurs
        var bidSize = RoundToStep(spec.TypicalTradeSize * (decimal)(0.5 + _random.NextDouble() * 3), inst.QuantityStep);
        var askSize = RoundToStep(spec.TypicalTradeSize * (decimal)(0.5 + _random.NextDouble() * 3), inst.QuantityStep);
        events.Add(new QuoteTick
        {
            Symbol = inst.Symbol, Source = SourceName, AsOfUtc = afterTrades, ReceivedUtc = now, DelayedMinutes = delayed, Quality = quality,
            Bid = bid, Ask = ask, BidSize = bidSize, AskSize = askSize, Last = st.LastTrade ?? st.Price,
            Volume24h = st.Volume, High24h = st.High, Low24h = st.Low,
            ChangePercent = st.SessionOpen > 0 ? Math.Round(((st.LastTrade ?? st.Price) - st.SessionOpen) / st.SessionOpen * 100m, 4) : null,
        });

        // Orderbuch nur für Krypto
        if (spec.IsDelayed) return;
        var newBids = new List<OrderBookLevel>(_options.BookDepth);
        var newAsks = new List<OrderBookLevel>(_options.BookDepth);
        for (int i = 0; i < _options.BookDepth; i++)
        {
            var q1 = RoundToStep(spec.TypicalTradeSize * (decimal)(0.2 + _random.NextDouble() * (1 + i * 0.3)), inst.QuantityStep);
            var q2 = RoundToStep(spec.TypicalTradeSize * (decimal)(0.2 + _random.NextDouble() * (1 + i * 0.3)), inst.QuantityStep);
            var bp = bid - tick * i;
            if (bp > 0 && q1 > 0) newBids.Add(new OrderBookLevel(bp, q1));
            if (q2 > 0) newAsks.Add(new OrderBookLevel(ask + tick * i, q2));
        }
        bool snapshot = st.Bids.Count == 0 || st.Steps % _options.BookSnapshotEvery == 0;
        events.Add(new OrderBookUpdate
        {
            Symbol = inst.Symbol, Source = SourceName, AsOfUtc = afterTrades, ReceivedUtc = now, Quality = quality,
            IsSnapshot = snapshot,
            Bids = snapshot ? newBids : Diff(st.Bids, newBids),
            Asks = snapshot ? newAsks : Diff(st.Asks, newAsks),
        });
        st.Bids = newBids;
        st.Asks = newAsks;
    }

    /// <summary>Delta zwischen zwei Buchseiten: entfernte Stufen mit Menge 0, geänderte/neue mit neuer Menge.</summary>
    internal static IReadOnlyList<OrderBookLevel> Diff(IReadOnlyList<OrderBookLevel> oldSide, IReadOnlyList<OrderBookLevel> newSide)
    {
        var oldMap = oldSide.ToDictionary(l => l.Price, l => l.Quantity);
        var newMap = newSide.ToDictionary(l => l.Price, l => l.Quantity);
        var delta = new List<OrderBookLevel>();
        foreach (var (p, _) in oldMap) if (!newMap.ContainsKey(p)) delta.Add(new OrderBookLevel(p, 0m));
        foreach (var (p, q) in newMap) if (!oldMap.TryGetValue(p, out var oq) || oq != q) delta.Add(new OrderBookLevel(p, q));
        return delta;
    }

    // ---------- Abfragen ----------
    public override bool Supports(string symbol) => _states.ContainsKey(symbol);

    public override Task<IReadOnlyList<Instrument>> SearchSymbolsAsync(string query, CancellationToken ct)
        => Task.FromResult(FilterInstruments(_options.Instruments.Select(s => s.Instrument), query));

    public override Task<OrderBookSnapshot?> GetOrderBookAsync(string symbol, int depth, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(symbol, out var st) || st.Spec.IsDelayed || st.Bids.Count == 0) return Task.FromResult<OrderBookSnapshot?>(null);
            return Task.FromResult<OrderBookSnapshot?>(new OrderBookSnapshot
            {
                Symbol = symbol, Source = SourceName, AsOfUtc = Clock.UtcNow,
                Bids = st.Bids.Take(depth).ToList(), Asks = st.Asks.Take(depth).ToList(),
            });
        }
    }

    /// <summary>
    /// Historie: vor Sitzungsbeginn reproduzierbar synthetisch (rückwärts vom Startkurs, Zufall aus dem Symbol abgeleitet),
    /// danach aus den simulierten Trades.
    /// </summary>
    public override Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, Timeframe timeframe, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, CancellationToken ct)
    {
        if (!_states.TryGetValue(symbol, out var st)) return Task.FromResult<IReadOnlyList<Candle>>([]);
        var now = Clock.UtcNow;
        var to = toUtc ?? now;
        var from = timeframe.Floor(fromUtc ?? to - timeframe.ToTimeSpan() * 500);
        var tfSpan = timeframe.ToTimeSpan();
        var anchor = timeframe.Floor(_sessionStartUtc - TimeSpan.FromMinutes(st.Spec.IsDelayed ? _options.StockDelayMinutes : 0));
        var result = new List<Candle>();

        lock (_lock)
        {
            // Synthetische Historie [from, anchor)
            if (from < anchor)
            {
                int needed = (int)Math.Min(_options.MaxHistoryCandles, Math.Ceiling((anchor - from) / tfSpan));
                var hist = History(st, timeframe, needed);
                result.AddRange(hist.Where(c => c.OpenTimeUtc >= from && c.OpenTimeUtc < to));
            }
            // Live-Teil aus simulierten Trades
            var live = new List<Candle>();
            if (_liveM1.TryGetValue(symbol, out var m1)) live.AddRange(m1);
            if (_m1.GetCurrent(symbol, Timeframe.M1) is { } cur) live.Add(cur);
            var liveTf = timeframe == Timeframe.M1 ? live : TradeCandleAggregator.Resample(live, timeframe).ToList();
            result.AddRange(liveTf.Where(c => c.OpenTimeUtc >= from && c.OpenTimeUtc < to && c.OpenTimeUtc >= anchor));
        }
        return Task.FromResult<IReadOnlyList<Candle>>(result.OrderBy(c => c.OpenTimeUtc).ToList());
    }

    private List<Candle> History(SymbolState st, Timeframe tf, int count)
    {
        var key = (st.Spec.Instrument.Symbol, tf);
        if (_historyCache.TryGetValue(key, out var cached) && cached.Count >= count) return cached;
        var symbol = st.Spec.Instrument.Symbol;
        var tick = st.Spec.Instrument.TickSize > 0 ? st.Spec.Instrument.TickSize : 0.01m;
        var rnd = new Random(StableHash(symbol) ^ (int)tf * 7919);
        var sigma = st.Spec.DailyVolatility * Math.Sqrt(tf.ToTimeSpan().TotalMinutes / 1440.0);
        var anchor = tf.Floor(_sessionStartUtc - TimeSpan.FromMinutes(st.Spec.IsDelayed ? _options.StockDelayMinutes : 0));
        var list = new List<Candle>(count);
        var close = st.Spec.StartPrice;
        for (int k = 1; k <= count; k++)
        {
            var z = Math.Sqrt(-2.0 * Math.Log(1.0 - rnd.NextDouble())) * Math.Cos(2.0 * Math.PI * rnd.NextDouble());
            var open = Math.Max(tick, RoundToStep(close / (1m + (decimal)(sigma * z)), tick));
            var wick = (decimal)(Math.Abs(sigma * 0.5 * rnd.NextDouble()));
            var high = RoundToStep(Math.Max(open, close) * (1m + wick), tick);
            var low = Math.Max(tick, RoundToStep(Math.Min(open, close) * (1m - wick), tick));
            list.Add(new Candle
            {
                Symbol = symbol, Timeframe = tf, OpenTimeUtc = anchor - tf.ToTimeSpan() * k,
                Open = open, High = Math.Max(high, Math.Max(open, close)), Low = Math.Min(low, Math.Min(open, close)), Close = close,
                Volume = RoundToStep(st.Spec.TypicalTradeSize * (decimal)(20 + rnd.NextDouble() * 200) * (decimal)Math.Sqrt(tf.ToTimeSpan().TotalMinutes), st.Spec.Instrument.QuantityStep),
                TradeCount = 20 + rnd.Next(200),
                Source = SourceName,
            });
            close = open;
        }
        list.Reverse();
        _historyCache[key] = list;
        return list;
    }

    private static int StableHash(string s)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (var c in s) { h ^= c; h *= 16777619; }
            return (int)h;
        }
    }
}
