using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;

namespace Musterdepot.IntegrationTests.Infrastructure;

/// <summary>Baut ein Orderbuch aus Snapshots und Deltas nach (Menge 0 = Stufe entfernen). Für Plausibilitätsprüfungen.</summary>
public sealed class OrderBookState
{
    private readonly SortedDictionary<decimal, decimal> _bids = new();
    private readonly SortedDictionary<decimal, decimal> _asks = new();

    public bool HasSnapshot { get; private set; }
    public decimal? BestBid => _bids.Count > 0 ? _bids.Keys.Last() : null;
    public decimal? BestAsk => _asks.Count > 0 ? _asks.Keys.First() : null;
    public decimal? BestBidSize => BestBid is { } b ? _bids[b] : null;
    public decimal? BestAskSize => BestAsk is { } a ? _asks[a] : null;
    public bool IsCrossed => BestBid is { } b && BestAsk is { } a && b >= a;
    public int BidLevels => _bids.Count;
    public int AskLevels => _asks.Count;

    public void Apply(OrderBookUpdate update)
    {
        if (update.IsSnapshot)
        {
            _bids.Clear();
            _asks.Clear();
            HasSnapshot = true;
        }
        Apply(_bids, update.Bids);
        Apply(_asks, update.Asks);
    }

    private static void Apply(SortedDictionary<decimal, decimal> side, IReadOnlyList<OrderBookLevel> levels)
    {
        foreach (var l in levels)
        {
            if (l.Quantity == 0) side.Remove(l.Price);
            else side[l.Price] = l.Quantity;
        }
    }
}
