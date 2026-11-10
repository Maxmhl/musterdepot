using System.Collections.Immutable;
using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;

namespace Musterdepot.Engine.Market;

/// <summary>Zuletzt bekannter Marktzustand eines Symbols (unveränderlich). Orderbuch wird aus Snapshot + Delta gepflegt.</summary>
public sealed record SymbolMarketState
{
    private static readonly IComparer<decimal> Descending = Comparer<decimal>.Create((a, b) => b.CompareTo(a));

    public required string Symbol { get; init; }
    public string Source { get; init; } = "";
    public int DelayedMinutes { get; init; }
    public DataQuality Quality { get; init; } = DataQuality.Realtime;

    public decimal? LastTradePrice { get; init; }
    public decimal? LastTradeQuantity { get; init; }
    public DateTimeOffset? LastTradeAsOfUtc { get; init; }

    public decimal? Bid { get; init; }
    public decimal? Ask { get; init; }
    public decimal? QuoteLast { get; init; }
    public DateTimeOffset? QuoteAsOfUtc { get; init; }

    public decimal? LastCandleClose { get; init; }
    public DateTimeOffset? CandleAsOfUtc { get; init; }

    /// <summary>Bids absteigend nach Preis.</summary>
    public ImmutableSortedDictionary<decimal, decimal> BookBids { get; init; } = ImmutableSortedDictionary.Create<decimal, decimal>(Descending);
    /// <summary>Asks aufsteigend nach Preis.</summary>
    public ImmutableSortedDictionary<decimal, decimal> BookAsks { get; init; } = ImmutableSortedDictionary<decimal, decimal>.Empty;
    public DateTimeOffset? BookAsOfUtc { get; init; }

    /// <summary>Jüngster Datenzeitpunkt (AsOfUtc) für dieses Symbol.</summary>
    public DateTimeOffset? LastAsOfUtc { get; init; }

    public bool IsDelayed => DelayedMinutes > 0 || Quality == DataQuality.Delayed;

    public decimal? BestBid => BookBids.Count > 0 && BookAsOfUtc >= (QuoteAsOfUtc ?? DateTimeOffset.MinValue) ? BookBids.Keys.First() : Bid ?? (BookBids.Count > 0 ? BookBids.Keys.First() : null);
    public decimal? BestAsk => BookAsks.Count > 0 && BookAsOfUtc >= (QuoteAsOfUtc ?? DateTimeOffset.MinValue) ? BookAsks.Keys.First() : Ask ?? (BookAsks.Count > 0 ? BookAsks.Keys.First() : null);

    /// <summary>Letzter bekannter Kurs: letzter Trade, sonst Quote-Last, sonst Mitte, sonst Kerzenschluss.</summary>
    public decimal? LastPrice
    {
        get
        {
            // Jüngste Information gewinnt (Trade vor Quote-Last vor Kerzenschluss bei Gleichstand).
            decimal? best = null;
            DateTimeOffset bestAt = DateTimeOffset.MinValue;
            void Consider(decimal? p, DateTimeOffset? at)
            {
                if (p is null || at is null) return;
                if (best is null || at.Value > bestAt) { best = p; bestAt = at.Value; }
            }
            Consider(LastTradePrice, LastTradeAsOfUtc);
            Consider(QuoteLast, QuoteAsOfUtc);
            Consider(LastCandleClose, CandleAsOfUtc);
            if (best is not null) return best;
            if (BestBid is { } b && BestAsk is { } a) return (a + b) / 2m;
            return BestAsk ?? BestBid;
        }
    }

    public SymbolMarketState Apply(MarketEvent e)
    {
        var s = this with
        {
            Source = e.Source,
            DelayedMinutes = e.DelayedMinutes,
            Quality = e is GapDetected ? Quality : e.Quality,
        };
        if (e is not GapDetected && e is not ConnectionStatusChanged && (s.LastAsOfUtc is null || e.AsOfUtc > s.LastAsOfUtc))
            s = s with { LastAsOfUtc = e.AsOfUtc };
        switch (e)
        {
            case TradeTick t:
                if (s.LastTradeAsOfUtc is null || t.AsOfUtc >= s.LastTradeAsOfUtc)
                    s = s with { LastTradePrice = t.Price, LastTradeQuantity = t.Quantity, LastTradeAsOfUtc = t.AsOfUtc };
                break;
            case QuoteTick q:
                s = s with
                {
                    Bid = q.Bid ?? s.Bid,
                    Ask = q.Ask ?? s.Ask,
                    QuoteLast = q.Last ?? s.QuoteLast,
                    QuoteAsOfUtc = q.AsOfUtc,
                };
                break;
            case OrderBookUpdate b:
                s = ApplyBook(s, b);
                break;
            case CandleUpdate c:
                s = s with { LastCandleClose = c.Candle.Close, CandleAsOfUtc = c.AsOfUtc };
                break;
            case GapDetected g when g.BackfilledCandles.Count > 0:
                var last = g.BackfilledCandles[^1];
                var candleEnd = last.OpenTimeUtc + last.Timeframe.ToTimeSpan();
                s = s with
                {
                    LastCandleClose = last.Close,
                    CandleAsOfUtc = candleEnd,
                    LastAsOfUtc = s.LastAsOfUtc is null || candleEnd > s.LastAsOfUtc ? candleEnd : s.LastAsOfUtc,
                };
                break;
        }
        return s;
    }

    private static SymbolMarketState ApplyBook(SymbolMarketState s, OrderBookUpdate b)
    {
        var bids = b.IsSnapshot ? s.BookBids.Clear() : s.BookBids;
        var asks = b.IsSnapshot ? s.BookAsks.Clear() : s.BookAsks;
        var bb = bids.ToBuilder();
        foreach (var l in b.Bids)
        {
            if (l.Quantity <= 0) bb.Remove(l.Price); else bb[l.Price] = l.Quantity;
        }
        var ab = asks.ToBuilder();
        foreach (var l in b.Asks)
        {
            if (l.Quantity <= 0) ab.Remove(l.Price); else ab[l.Price] = l.Quantity;
        }
        return s with { BookBids = bb.ToImmutable(), BookAsks = ab.ToImmutable(), BookAsOfUtc = b.AsOfUtc };
    }

    public OrderBookSnapshot? ToOrderBookSnapshot(int depth = int.MaxValue)
    {
        if (BookAsOfUtc is null) return null;
        return new OrderBookSnapshot
        {
            Symbol = Symbol,
            Source = Source,
            AsOfUtc = BookAsOfUtc.Value,
            Bids = BookBids.Take(depth).Select(kv => new OrderBookLevel(kv.Key, kv.Value)).ToList(),
            Asks = BookAsks.Take(depth).Select(kv => new OrderBookLevel(kv.Key, kv.Value)).ToList(),
        };
    }
}

/// <summary>Marktzustand aller Symbole (unveränderlich). Wird nicht aus Domain-Events rekonstruiert.</summary>
public sealed record MarketState
{
    public static MarketState Empty { get; } = new();

    public ImmutableSortedDictionary<string, SymbolMarketState> Symbols { get; init; } =
        ImmutableSortedDictionary.Create<string, SymbolMarketState>(StringComparer.Ordinal);

    public SymbolMarketState? Get(string symbol) => Symbols.TryGetValue(symbol, out var s) ? s : null;

    public MarketState Apply(MarketEvent e)
    {
        if (e is ConnectionStatusChanged || string.IsNullOrEmpty(e.Symbol) || e.Symbol == "*") return this;
        var current = Get(e.Symbol) ?? new SymbolMarketState { Symbol = e.Symbol };
        return this with { Symbols = Symbols.SetItem(e.Symbol, current.Apply(e)) };
    }

    public decimal? LastPrice(string symbol) => Get(symbol)?.LastPrice;

    /// <summary>
    /// Näherungskurs aus Marktdaten (z. B. USDC/EUR bei Kraken): 1 <paramref name="from"/> in <paramref name="to"/>.
    /// </summary>
    public (decimal Rate, string Source)? FxFromMarket(string from, string to)
    {
        string[] aliases = from == "USD" ? ["USD", "USDC", "USDT"] : [from];
        foreach (var a in aliases)
        {
            if (LastPrice($"{a}/{to}") is { } direct && direct > 0) return (direct, $"market:{a}/{to}");
            if (LastPrice($"{to}/{a}") is { } inverse && inverse > 0) return (1m / inverse, $"market:{to}/{a}");
        }
        return null;
    }
}
