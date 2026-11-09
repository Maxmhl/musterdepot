namespace Musterdepot.Core.Domain;

/// <summary>Handelbares Instrument. Symbol ist der programmweite Schlüssel, z. B. "BTC/EUR" oder für Aktien die ISIN "DE0007164600".</summary>
public sealed record Instrument
{
    public required string Symbol { get; init; }
    public required string Name { get; init; }
    public required AssetClass AssetClass { get; init; }
    /// <summary>Währung, in der der Preis notiert (z. B. EUR, USD).</summary>
    public required string QuoteCurrency { get; init; }
    /// <summary>Basis-Asset bei Krypto (BTC), sonst Symbol/ISIN.</summary>
    public string? BaseAsset { get; init; }
    public string? Isin { get; init; }
    public string? Wkn { get; init; }
    /// <summary>Kleinster Preisschritt.</summary>
    public decimal TickSize { get; init; } = 0.01m;
    /// <summary>Kleinster Mengenschritt (Krypto z. B. 0.00000001, Aktien 1).</summary>
    public decimal QuantityStep { get; init; } = 1m;
    public decimal MinQuantity { get; init; }
    /// <summary>Primäre Datenquelle, z. B. "kraken", "bitvavo", "gettex".</summary>
    public string? PrimarySource { get; init; }
    /// <summary>Symbol, wie es die Quelle erwartet (z. B. "BTC/EUR" bei Kraken, "BTC-EUR" bei Bitvavo).</summary>
    public string? SourceSymbol { get; init; }
}
