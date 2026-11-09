using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;

namespace Musterdepot.MarketData.Infrastructure;

/// <summary>
/// Gepflegtes Orderbuch: Snapshot setzen, Deltas anwenden (Menge 0 = Stufe entfernen), auf maximale Tiefe kürzen.
/// Thread-sicher.
/// </summary>
public sealed class OrderBookState
{
    private sealed class Descending : IComparer<decimal>
    {
        public static readonly Descending Instance = new();
        public int Compare(decimal x, decimal y) => y.CompareTo(x);
    }

    private readonly SortedDictionary<decimal, decimal> _bids = new(Descending.Instance);
    private readonly SortedDictionary<decimal, decimal> _asks = new();
    private readonly Lock _lock = new();

    public OrderBookState(int maxDepth = 1000) => MaxDepth = maxDepth > 0 ? maxDepth : int.MaxValue;

    public int MaxDepth { get; }
    public bool HasSnapshot { get; private set; }
    public DateTimeOffset LastUpdateUtc { get; private set; }
    public string? Source { get; private set; }

    public int BidCount { get { lock (_lock) return _bids.Count; } }
    public int AskCount { get { lock (_lock) return _asks.Count; } }

    public void Clear()
    {
        lock (_lock)
        {
            _bids.Clear();
            _asks.Clear();
            HasSnapshot = false;
        }
    }

    public void Apply(OrderBookUpdate update)
    {
        if (update.IsSnapshot) ApplySnapshot(update.Bids, update.Asks, update.AsOfUtc, update.Source);
        else ApplyDelta(update.Bids, update.Asks, update.AsOfUtc, update.Source);
    }

    public void ApplySnapshot(IEnumerable<OrderBookLevel> bids, IEnumerable<OrderBookLevel> asks, DateTimeOffset asOfUtc = default, string? source = null)
    {
        lock (_lock)
        {
            _bids.Clear();
            _asks.Clear();
            foreach (var l in bids) if (l.Quantity > 0) _bids[l.Price] = l.Quantity;
            foreach (var l in asks) if (l.Quantity > 0) _asks[l.Price] = l.Quantity;
            Truncate();
            HasSnapshot = true;
            LastUpdateUtc = asOfUtc;
            Source = source ?? Source;
        }
    }

    public void ApplyDelta(IEnumerable<OrderBookLevel> bids, IEnumerable<OrderBookLevel> asks, DateTimeOffset asOfUtc = default, string? source = null)
    {
        lock (_lock)
        {
            foreach (var l in bids) Set(_bids, l);
            foreach (var l in asks) Set(_asks, l);
            Truncate();
            if (asOfUtc > LastUpdateUtc) LastUpdateUtc = asOfUtc;
            Source = source ?? Source;
        }
    }

    private static void Set(SortedDictionary<decimal, decimal> side, OrderBookLevel l)
    {
        if (l.Quantity <= 0) side.Remove(l.Price);
        else side[l.Price] = l.Quantity;
    }

    private void Truncate()
    {
        TruncateSide(_bids);
        TruncateSide(_asks);
    }

    private void TruncateSide(SortedDictionary<decimal, decimal> side)
    {
        if (side.Count <= MaxDepth) return;
        var remove = side.Keys.Skip(MaxDepth).ToList();
        foreach (var k in remove) side.Remove(k);
    }

    /// <summary>Beste Bids absteigend.</summary>
    public IReadOnlyList<OrderBookLevel> TopBids(int n)
    {
        lock (_lock) return _bids.Take(n).Select(kv => new OrderBookLevel(kv.Key, kv.Value)).ToList();
    }

    /// <summary>Beste Asks aufsteigend.</summary>
    public IReadOnlyList<OrderBookLevel> TopAsks(int n)
    {
        lock (_lock) return _asks.Take(n).Select(kv => new OrderBookLevel(kv.Key, kv.Value)).ToList();
    }

    public OrderBookSnapshot ToSnapshot(string symbol, string? source = null, DateTimeOffset? asOfUtc = null, int depth = int.MaxValue)
    {
        lock (_lock)
        {
            return new OrderBookSnapshot
            {
                Symbol = symbol,
                Source = source ?? Source ?? "",
                AsOfUtc = asOfUtc ?? LastUpdateUtc,
                Bids = _bids.Take(depth).Select(kv => new OrderBookLevel(kv.Key, kv.Value)).ToList(),
                Asks = _asks.Take(depth).Select(kv => new OrderBookLevel(kv.Key, kv.Value)).ToList(),
            };
        }
    }
}
