using System.Text.Json;
using System.Text.Json.Serialization;

namespace Musterdepot.Charting;

/// <summary>Eine Kerze, wie sie an Lightweight Charts geht (time = Unix-Sekunden UTC).</summary>
public sealed record ChartBar(long Time, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume, bool Backfill = false);

/// <summary>Ein Punkt einer Linien-Serie.</summary>
public sealed record ChartPoint(long Time, decimal Value);

/// <summary>Fertig berechnete Indikator-Serie. Pane 0 = Kurs-Chart, Pane 1 = Unterfenster (z. B. RSI).</summary>
public sealed record IndicatorSeries
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public int Pane { get; init; }
    public required string Color { get; init; }
    public int LineWidth { get; init; } = 2;
    /// <summary>Waagerechte Hilfslinien (z. B. 70/30 beim RSI).</summary>
    public IReadOnlyList<decimal> Levels { get; init; } = [];
    public required IReadOnlyList<ChartPoint> Data { get; init; }
}

/// <summary>Letzter Punkt einer Indikator-Serie (für Live-Updates).</summary>
public sealed record IndicatorPoint(string Id, long Time, decimal Value);

/// <summary>Preislinie für eigene Orders und Positionen.</summary>
public sealed record ChartPriceLine
{
    public required string Id { get; init; }
    public required decimal Price { get; init; }
    public required string Title { get; init; }
    public required string Color { get; init; }
    /// <summary>0 = durchgezogen, 1 = gepunktet, 2 = gestrichelt (LineStyle von Lightweight Charts).</summary>
    public int LineStyle { get; init; } = 2;
    public int LineWidth { get; init; } = 1;
}

/// <summary>Markierte Datenlücke (Standby, Verbindungsabbruch). Start/Ende in Unix-Sekunden.</summary>
public sealed record ChartGap(long Start, long End, string Label);

/// <summary>Nachrichten C# → JavaScript. Werden per PostWebMessageAsJson an chart.js übergeben.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ThemeMessage), "theme")]
[JsonDerivedType(typeof(LoadMessage), "load")]
[JsonDerivedType(typeof(BarMessage), "bar")]
[JsonDerivedType(typeof(IndicatorsMessage), "indicators")]
[JsonDerivedType(typeof(PriceLinesMessage), "priceLines")]
[JsonDerivedType(typeof(GapsMessage), "gaps")]
[JsonDerivedType(typeof(StatusMessage), "status")]
public abstract record ChartMessage
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>JSON mit Diskriminator "type" und camelCase-Feldern.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, typeof(ChartMessage), Options);
}

public sealed record ThemeMessage(bool Dark) : ChartMessage;

public sealed record LoadMessage : ChartMessage
{
    public required string Symbol { get; init; }
    public required string Timeframe { get; init; }
    public int PriceDecimals { get; init; } = 2;
    public required IReadOnlyList<ChartBar> Bars { get; init; }
    public IReadOnlyList<IndicatorSeries> Indicators { get; init; } = [];
    public IReadOnlyList<ChartGap> Gaps { get; init; } = [];
    public IReadOnlyList<ChartPriceLine> PriceLines { get; init; } = [];
    /// <summary>Hinweistext oben im Chart, z. B. "verzögert 15 Min".</summary>
    public string? Notice { get; init; }
}

public sealed record BarMessage(ChartBar Bar, IReadOnlyList<IndicatorPoint> Points) : ChartMessage;

public sealed record IndicatorsMessage(IReadOnlyList<IndicatorSeries> Indicators) : ChartMessage;

public sealed record PriceLinesMessage(IReadOnlyList<ChartPriceLine> Lines) : ChartMessage;

public sealed record GapsMessage(IReadOnlyList<ChartGap> Gaps) : ChartMessage;

public sealed record StatusMessage(string? Text) : ChartMessage;
