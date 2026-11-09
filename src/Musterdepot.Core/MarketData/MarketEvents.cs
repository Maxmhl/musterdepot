using Musterdepot.Core.Domain;

namespace Musterdepot.Core.MarketData;

/// <summary>
/// Schnittstelle A: einheitlicher Strom aus Trades, Kursen und Orderbuch-Updates.
/// Jedes Ereignis trägt Source, AsOfUtc, DelayedMinutes und ein Qualitätsflag.
/// </summary>
public abstract record MarketEvent
{
    public required string Symbol { get; init; }
    /// <summary>Datenquelle, z. B. "kraken", "bitvavo", "coinbase", "gettex", "fake", "replay".</summary>
    public required string Source { get; init; }
    /// <summary>Zeitpunkt, zu dem der Markt diesen Zustand hatte (UTC).</summary>
    public required DateTimeOffset AsOfUtc { get; init; }
    /// <summary>Zeitpunkt des Empfangs im Programm (UTC).</summary>
    public DateTimeOffset ReceivedUtc { get; init; }
    /// <summary>Verzögerung der Quelle in Minuten (0 = Echtzeit).</summary>
    public int DelayedMinutes { get; init; }
    public DataQuality Quality { get; init; } = DataQuality.Realtime;
    public bool IsRealtime => DelayedMinutes == 0 && Quality == DataQuality.Realtime;
}

public sealed record TradeTick : MarketEvent
{
    public required decimal Price { get; init; }
    public required decimal Quantity { get; init; }
    /// <summary>Aggressor-Seite, falls bekannt.</summary>
    public OrderSide? AggressorSide { get; init; }
    public string? TradeId { get; init; }
}

public sealed record QuoteTick : MarketEvent
{
    public decimal? Bid { get; init; }
    public decimal? Ask { get; init; }
    public decimal? Last { get; init; }
    public decimal? BidSize { get; init; }
    public decimal? AskSize { get; init; }
    public decimal? Volume24h { get; init; }
    /// <summary>Änderung in Prozent (24 h bei Krypto, ggü. Vortag bei Aktien).</summary>
    public decimal? ChangePercent { get; init; }
    public decimal? High24h { get; init; }
    public decimal? Low24h { get; init; }
}

public sealed record OrderBookUpdate : MarketEvent
{
    /// <summary>true = vollständiger Snapshot, false = Delta (Menge 0 = Stufe entfernen).</summary>
    public required bool IsSnapshot { get; init; }
    public required IReadOnlyList<OrderBookLevel> Bids { get; init; }
    public required IReadOnlyList<OrderBookLevel> Asks { get; init; }
    public long? Checksum { get; init; }
}

public sealed record CandleUpdate : MarketEvent
{
    public required Candle Candle { get; init; }
}

/// <summary>Verbindungsstatus einer Quelle. Symbol = "*" für die ganze Quelle.</summary>
public sealed record ConnectionStatusChanged : MarketEvent
{
    public required ConnectionState State { get; init; }
    public string? Message { get; init; }
}

/// <summary>Eine Datenlücke wurde erkannt und (teilweise) per REST nachgeladen.</summary>
public sealed record GapDetected : MarketEvent
{
    public required DateTimeOffset GapStartUtc { get; init; }
    public required DateTimeOffset GapEndUtc { get; init; }
    /// <summary>Nachgeladene Kerzen in zeitlicher Reihenfolge (für die Prüfung offener Stop-/Limit-Orders).</summary>
    public IReadOnlyList<Candle> BackfilledCandles { get; init; } = [];
}

/// <summary>Aktueller Kurs inkl. Herkunft – das, was die KI per get_quote bekommt.</summary>
public sealed record QuoteSnapshot
{
    public required string Symbol { get; init; }
    public required string Source { get; init; }
    public required DateTimeOffset AsOfUtc { get; init; }
    public int DelayedMinutes { get; init; }
    public bool IsRealtime { get; init; }
    public DataQuality Quality { get; init; }
    public decimal? Bid { get; init; }
    public decimal? Ask { get; init; }
    public decimal? Last { get; init; }
    public decimal? ChangePercent { get; init; }
    public decimal? Volume24h { get; init; }
    public string Currency { get; init; } = "EUR";
    public decimal? Mid => Bid is { } b && Ask is { } a ? (b + a) / 2m : Last;
}
