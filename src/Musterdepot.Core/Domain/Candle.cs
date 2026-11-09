namespace Musterdepot.Core.Domain;

public sealed record Candle
{
    public required string Symbol { get; init; }
    public required Timeframe Timeframe { get; init; }
    /// <summary>Beginn der Kerze in UTC.</summary>
    public required DateTimeOffset OpenTimeUtc { get; init; }
    public required decimal Open { get; init; }
    public required decimal High { get; init; }
    public required decimal Low { get; init; }
    public required decimal Close { get; init; }
    public decimal Volume { get; init; }
    public int TradeCount { get; init; }
    public string Source { get; init; } = "";
    /// <summary>Kerze wurde nach einer Lücke (Standby, Verbindungsabbruch) per REST nachgeladen.</summary>
    public bool IsBackfill { get; init; }
    /// <summary>Kerze ist abgeschlossen.</summary>
    public bool IsClosed { get; init; } = true;
}

public sealed record OrderBookLevel(decimal Price, decimal Quantity);

public sealed record OrderBookSnapshot
{
    public required string Symbol { get; init; }
    public required string Source { get; init; }
    public required DateTimeOffset AsOfUtc { get; init; }
    /// <summary>Absteigend nach Preis.</summary>
    public required IReadOnlyList<OrderBookLevel> Bids { get; init; }
    /// <summary>Aufsteigend nach Preis.</summary>
    public required IReadOnlyList<OrderBookLevel> Asks { get; init; }
    public decimal? BestBid => Bids.Count > 0 ? Bids[0].Price : null;
    public decimal? BestAsk => Asks.Count > 0 ? Asks[0].Price : null;
}
