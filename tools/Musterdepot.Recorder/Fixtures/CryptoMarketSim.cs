using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;

namespace Musterdepot.Recorder.Fixtures;

/// <summary>
/// Zufall nur über ganze Zahlen und decimal (keine Gleitkomma-Transzendenten), damit die Fixtures auf jedem
/// Betriebssystem und jeder CPU bitgleich entstehen.
/// </summary>
internal sealed class DeterministicRandom(int seed)
{
    private readonly Random _r = new(seed);

    public int Next(int minInclusive, int maxExclusive) => _r.Next(minInclusive, maxExclusive);
    public bool Chance(int percent) => _r.Next(100) < percent;

    /// <summary>Näherungsweise N(0,1) (Irwin-Hall aus 12 ganzzahligen Gleichverteilungen), 3 Nachkommastellen.</summary>
    public decimal Normal()
    {
        int sum = 0;
        for (int i = 0; i < 12; i++) sum += _r.Next(0, 1001);
        return (sum - 6000) / 1000m;
    }

    /// <summary>Gleichverteilt in [min, max] mit <paramref name="decimals"/> Nachkommastellen.</summary>
    public decimal Uniform(decimal min, decimal max, int decimals)
    {
        decimal scale = 1m;
        for (int i = 0; i < decimals; i++) scale *= 10m;
        long lo = (long)(min * scale), hi = (long)(max * scale);
        return _r.NextInt64(lo, hi + 1) / scale;
    }
}

/// <summary>
/// Einfacher, aber in sich stimmiger Krypto-Markt: Mittelkurs als Zufallspfad, Orderbuch aus Top-Stufe plus
/// 5-EUR-Raster, Trades zum besten Bid/Ask, die das Buch abbauen, M1-Kerzen aus den Trades, Quotes aus dem Buch.
/// </summary>
internal sealed class CryptoMarketSim
{
    private const decimal Tick = 0.1m;
    private const decimal Grid = 5m;
    private const int DepthLevels = 10;

    private readonly DeterministicRandom _rng;
    private readonly string _symbol;
    private readonly string _source;
    private readonly SortedDictionary<decimal, decimal> _bids = new();
    private readonly SortedDictionary<decimal, decimal> _asks = new();
    /// <summary>Stufen, deren Menge seit dem letzten Buch-Update durch Trades gesunken ist.</summary>
    private readonly HashSet<decimal> _dirtyBids = [], _dirtyAsks = [];
    private decimal _anchor;
    private DateTimeOffset _lastReceived;
    private long _tradeId;

    private DateTimeOffset _candleStart;
    private bool _candleHasTrade;
    private decimal _o, _h, _l, _c, _v;
    private int _n;

    private readonly decimal _open24h;
    private decimal _high24h, _low24h, _vol24h;

    public CryptoMarketSim(int seed, string symbol, string source, decimal startMid, long firstTradeId)
    {
        _rng = new DeterministicRandom(seed);
        _symbol = symbol;
        _source = source;
        Mid = startMid;
        _anchor = startMid;
        _tradeId = firstTradeId;
        _open24h = RoundTick(startMid * 0.988m);
        _high24h = RoundTick(startMid + 350m);
        _low24h = RoundTick(_open24h - 120m);
        _vol24h = 1234.56789012m;
        LastTradePrice = RoundTick(startMid);
    }

    public DeterministicRandom Rng => _rng;
    public List<MarketEvent> Events { get; } = [];
    public decimal Mid { get; private set; }
    public decimal LastTradePrice { get; private set; }
    public decimal BestBid => _bids.Keys.Last();
    public decimal BestAsk => _asks.Keys.First();

    public static decimal RoundTick(decimal price) => Math.Round(price / Tick, MidpointRounding.AwayFromZero) * Tick;

    /// <summary>Zufallsschritt mit leichter Rückkehr zum Anker (ruhiger Markt).</summary>
    public void Walk(decimal sigma, decimal reversion = 0.002m) =>
        Mid = Math.Round(Mid + _rng.Normal() * sigma + (_anchor - Mid) * reversion, 2);

    /// <summary>Trend: verschiebt den Anker, zu dem der Kurs zurückkehrt.</summary>
    public void Drift(decimal delta) => _anchor += delta;

    /// <summary>Kurssprung: Mittelkurs und Anker springen sofort.</summary>
    public void Jump(decimal delta)
    {
        Mid += delta;
        _anchor += delta;
    }

    private T Stamp<T>(T e, DateTimeOffset asOf) where T : MarketEvent
    {
        var received = asOf.AddMilliseconds(_rng.Next(25, 90));
        if (received < _lastReceived) received = _lastReceived;
        _lastReceived = received;
        MarketEvent baseEvent = e;
        var stamped = (T)(baseEvent with { ReceivedUtc = received });
        Events.Add(stamped);
        return stamped;
    }

    public void Status(DateTimeOffset t, ConnectionState state, string message) =>
        Stamp(new ConnectionStatusChanged { Symbol = "*", Source = _source, AsOfUtc = t, State = state, Message = message }, t);

    /// <summary>Buch neu ausrichten und als Snapshot oder Delta (nur geänderte Stufen, Menge 0 = entfernt) melden.</summary>
    public void Book(DateTimeOffset t, bool snapshot)
    {
        var bb = Math.Floor((Mid - Tick / 2) / Tick) * Tick;
        var spreadTicks = _rng.Chance(80) ? 1 : _rng.Next(2, 5);
        var ba = bb + spreadTicks * Tick;

        var targetBids = new List<decimal> { bb };
        var g = Math.Floor((bb - Tick) / Grid) * Grid;
        for (int k = 0; targetBids.Count < DepthLevels; k++) targetBids.Add(g - k * Grid);
        var targetAsks = new List<decimal> { ba };
        g = Math.Ceiling((ba + Tick) / Grid) * Grid;
        for (int k = 0; targetAsks.Count < DepthLevels; k++) targetAsks.Add(g + k * Grid);

        var bidChanges = Align(_bids, targetBids, _dirtyBids, isTop: p => p == bb);
        var askChanges = Align(_asks, targetAsks, _dirtyAsks, isTop: p => p == ba);

        if (snapshot)
        {
            Stamp(new OrderBookUpdate
            {
                Symbol = _symbol, Source = _source, AsOfUtc = t, IsSnapshot = true,
                Bids = _bids.Reverse().Select(kv => new OrderBookLevel(kv.Key, kv.Value)).ToList(),
                Asks = _asks.Select(kv => new OrderBookLevel(kv.Key, kv.Value)).ToList(),
            }, t);
        }
        else if (bidChanges.Count + askChanges.Count > 0)
        {
            Stamp(new OrderBookUpdate
            {
                Symbol = _symbol, Source = _source, AsOfUtc = t, IsSnapshot = false,
                Bids = bidChanges.OrderByDescending(l => l.Price).ToList(),
                Asks = askChanges.OrderBy(l => l.Price).ToList(),
            }, t);
        }
    }

    private List<OrderBookLevel> Align(SortedDictionary<decimal, decimal> side, List<decimal> target, HashSet<decimal> dirty, Func<decimal, bool> isTop)
    {
        var changes = new List<OrderBookLevel>();
        var targetSet = target.ToHashSet();
        foreach (var price in side.Keys.Where(p => !targetSet.Contains(p)).ToList())
        {
            side.Remove(price);
            changes.Add(new OrderBookLevel(price, 0m));
        }
        foreach (var price in target)
        {
            if (side.ContainsKey(price)) continue;
            var qty = isTop(price) ? _rng.Uniform(0.01m, 0.9m, 8) : _rng.Uniform(0.05m, 4m, 8);
            side[price] = qty;
            changes.Add(new OrderBookLevel(price, qty));
        }
        // Gelegentlich ändern Teilnehmer ihre Gebote auf bestehenden Stufen.
        if (_rng.Chance(30))
        {
            var price = target[_rng.Next(0, target.Count)];
            if (!changes.Any(c => c.Price == price))
            {
                var qty = _rng.Uniform(0.05m, 4m, 8);
                side[price] = qty;
                changes.Add(new OrderBookLevel(price, qty));
            }
        }
        foreach (var price in dirty.Order())
            if (side.TryGetValue(price, out var qty) && !changes.Any(c => c.Price == price))
                changes.Add(new OrderBookLevel(price, qty));
        dirty.Clear();
        return changes;
    }

    /// <summary>Trade zum besten Preis der Gegenseite; verringert die Menge der Top-Stufe.</summary>
    public TradeTick Trade(DateTimeOffset t, OrderSide? forcedSide = null)
    {
        var side = forcedSide ?? (_rng.Chance(50) ? OrderSide.Buy : OrderSide.Sell);
        var book = side == OrderSide.Buy ? _asks : _bids;
        var price = side == OrderSide.Buy ? BestAsk : BestBid;
        var r = _rng.Next(0, 100);
        var qty = r < 70 ? _rng.Uniform(0.0001m, 0.02m, 8) : r < 95 ? _rng.Uniform(0.02m, 0.2m, 8) : _rng.Uniform(0.2m, 0.8m, 8);
        var cap = Math.Round(book[price] * 0.9m, 8, MidpointRounding.ToZero);
        if (qty > cap) qty = cap;
        if (qty < 0.00001m) qty = 0.00001m;
        book[price] = Math.Max(0.00001m, Math.Round(book[price] - qty, 8));
        (side == OrderSide.Buy ? _dirtyAsks : _dirtyBids).Add(price);
        Mid += side == OrderSide.Buy ? 0.3m : -0.3m;

        if (!_candleHasTrade)
        {
            _o = _h = _l = price;
            _candleHasTrade = true;
        }
        _h = Math.Max(_h, price);
        _l = Math.Min(_l, price);
        _c = price;
        _v += qty;
        _n++;
        LastTradePrice = price;
        _vol24h += qty;
        _high24h = Math.Max(_high24h, price);
        _low24h = Math.Min(_low24h, price);

        return Stamp(new TradeTick
        {
            Symbol = _symbol, Source = _source, AsOfUtc = t,
            Price = price, Quantity = qty, AggressorSide = side, TradeId = (++_tradeId).ToString(System.Globalization.CultureInfo.InvariantCulture),
        }, t);
    }

    public void Quote(DateTimeOffset t) => Stamp(new QuoteTick
    {
        Symbol = _symbol, Source = _source, AsOfUtc = t,
        Bid = BestBid, Ask = BestAsk, BidSize = _bids[BestBid], AskSize = _asks[BestAsk],
        Last = LastTradePrice,
        Volume24h = Math.Round(_vol24h, 8),
        ChangePercent = Math.Round((LastTradePrice - _open24h) / _open24h * 100m, 2),
        High24h = _high24h, Low24h = _low24h,
    }, t);

    public void StartCandle(DateTimeOffset minuteStart)
    {
        _candleStart = minuteStart;
        _candleHasTrade = false;
        _o = _h = _l = _c = LastTradePrice;
        _v = 0;
        _n = 0;
    }

    /// <summary>Schließt die laufende M1-Kerze (ohne Trades: flach auf dem letzten Preis) und beginnt die nächste.</summary>
    public void CloseCandle(DateTimeOffset emitAt)
    {
        Stamp(new CandleUpdate
        {
            Symbol = _symbol, Source = _source, AsOfUtc = emitAt,
            Candle = new Candle
            {
                Symbol = _symbol, Timeframe = Timeframe.M1, OpenTimeUtc = _candleStart,
                Open = _o, High = _h, Low = _l, Close = _c, Volume = _v, TradeCount = _n,
                Source = _source, IsClosed = true,
            },
        }, emitAt);
        StartCandle(_candleStart.AddMinutes(1));
    }

    /// <summary>Simuliert den Markt während einer Lücke (nichts wird gemeldet) und liefert die nachladbaren M1-Kerzen.</summary>
    public List<Candle> SimulateBackfill(DateTimeOffset from, IReadOnlyList<decimal> driftPerMinute, decimal sigma)
    {
        var candles = new List<Candle>();
        for (int m = 0; m < driftPerMinute.Count; m++)
        {
            var open = RoundTick(Mid);
            decimal high = open, low = open;
            for (int s = 0; s < 60; s++)
            {
                Drift(driftPerMinute[m] / 60m);
                Walk(sigma, 0.05m);
                var p = RoundTick(Mid);
                high = Math.Max(high, p);
                low = Math.Min(low, p);
            }
            var close = RoundTick(Mid);
            candles.Add(new Candle
            {
                Symbol = _symbol, Timeframe = Timeframe.M1, OpenTimeUtc = from.AddMinutes(m),
                Open = open, High = high, Low = low, Close = close,
                Volume = _rng.Uniform(2m, 18m, 8), TradeCount = _rng.Next(60, 400),
                Source = _source, IsBackfill = true, IsClosed = true,
            });
            _high24h = Math.Max(_high24h, high);
            _low24h = Math.Min(_low24h, low);
            LastTradePrice = close;
        }
        return candles;
    }

    public void Gap(DateTimeOffset t, DateTimeOffset gapStart, DateTimeOffset gapEnd, IReadOnlyList<Candle> backfill) =>
        Stamp(new GapDetected
        {
            Symbol = _symbol, Source = _source, AsOfUtc = t,
            GapStartUtc = gapStart, GapEndUtc = gapEnd, BackfilledCandles = backfill,
        }, t);

    /// <summary>Eine Sekunde ruhiger Handel: Zufallsschritt, 0–2 Trades, Buch-Delta, jede 2. Sekunde ein Quote.</summary>
    public void Second(DateTimeOffset t, int index, decimal sigma, int firstTradeOffsetMs = 150)
    {
        Walk(sigma);
        var r = _rng.Next(0, 100);
        var trades = r < 45 ? 0 : r < 85 ? 1 : 2;
        for (int k = 0; k < trades; k++)
            Trade(t.AddMilliseconds(firstTradeOffsetMs + k * 200 + _rng.Next(0, 40)));
        Book(t.AddMilliseconds(620), snapshot: false);
        if (index % 2 == 1) Quote(t.AddMilliseconds(900));
    }
}
