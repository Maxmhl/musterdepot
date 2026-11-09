using System.Globalization;
using System.Text;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;

namespace Musterdepot.Charting;

/// <summary>Format eines CSV-Exports.</summary>
public sealed record CsvFormat
{
    public required char Separator { get; init; }
    public required CultureInfo Culture { get; init; }
    public required string DateTimeFormat { get; init; }
    /// <summary>Zeitzone der ausgegebenen Zeitstempel.</summary>
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Utc;
    /// <summary>UTF-8 mit BOM schreiben (damit Excel Umlaute erkennt).</summary>
    public bool Utf8Bom { get; init; }
    public string NewLine { get; init; } = "\r\n";

    /// <summary>Maschinenlesbar: Komma, Punkt als Dezimaltrenner, ISO-8601 in UTC.</summary>
    public static CsvFormat Invariant { get; } = new()
    {
        Separator = ',',
        Culture = CultureInfo.InvariantCulture,
        DateTimeFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'",
        TimeZone = TimeZoneInfo.Utc,
        Utf8Bom = false,
    };

    /// <summary>Für deutsches Excel: Semikolon, Komma als Dezimaltrenner, dd.MM.yyyy HH:mm:ss in Ortszeit, UTF-8 mit BOM.</summary>
    public static CsvFormat GermanExcel(TimeZoneInfo? zone = null) => new()
    {
        Separator = ';',
        Culture = CultureInfo.GetCultureInfo("de-DE"),
        DateTimeFormat = "dd.MM.yyyy HH:mm:ss",
        TimeZone = zone ?? TimeZoneInfo.Local,
        Utf8Bom = true,
    };

    public Encoding Encoding => new UTF8Encoding(Utf8Bom);
}

/// <summary>Einfacher CSV-Schreiber mit korrekter Maskierung und Schutz vor Formel-Injektion in Textfeldern.</summary>
public sealed class CsvBuilder(CsvFormat format)
{
    private readonly StringBuilder _sb = new();
    private readonly List<string> _row = [];

    public CsvFormat Format { get; } = format;

    public CsvBuilder Text(string? value)
    {
        var v = value ?? "";
        // Excel/LibreOffice würden =, +, -, @ am Anfang als Formel deuten (z. B. aus KI-Strategie-Tags).
        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@' or '\t' or '\r') v = "'" + v;
        _row.Add(Escape(v));
        return this;
    }

    public CsvBuilder Number(decimal? value)
    {
        _row.Add(value is { } d ? Escape(d.ToString(Format.Culture)) : "");
        return this;
    }

    public CsvBuilder Integer(long? value)
    {
        _row.Add(value is { } d ? d.ToString(Format.Culture) : "");
        return this;
    }

    public CsvBuilder Time(DateTimeOffset? value)
    {
        if (value is not { } t) { _row.Add(""); return this; }
        var local = TimeZoneInfo.ConvertTime(t, Format.TimeZone);
        _row.Add(Escape(local.ToString(Format.DateTimeFormat, CultureInfo.InvariantCulture)));
        return this;
    }

    public CsvBuilder Bool(bool value) => Text(value ? "ja" : "nein");

    public CsvBuilder EndRow()
    {
        _sb.Append(string.Join(Format.Separator, _row)).Append(Format.NewLine);
        _row.Clear();
        return this;
    }

    public CsvBuilder Header(params string[] names)
    {
        foreach (var n in names) _row.Add(Escape(n));
        return EndRow();
    }

    public override string ToString() => _sb.ToString();

    private string Escape(string v)
    {
        if (v.IndexOfAny([Format.Separator, '"', '\r', '\n']) >= 0)
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        return v;
    }
}

/// <summary>CSV-Export für Ausführungen, Orders und Protokoll.</summary>
public static class TradeCsv
{
    public static string Side(OrderSide s) => s == OrderSide.Buy ? "Kauf" : "Verkauf";
    public static string Source(CommandSource s) => s == CommandSource.Human ? "Mensch" : "KI";

    public static string Quality(FillQuality q) => q switch
    {
        FillQuality.OrderBook => "Orderbuch",
        FillQuality.Trade => "Trade",
        FillQuality.Synthetic => "SYNTHETIC",
        FillQuality.Gap => "Lücke",
        _ => q.ToString(),
    };

    public static string Fills(IEnumerable<Fill> fills, CsvFormat format)
    {
        var b = new CsvBuilder(format);
        b.Header("Zeit", "Symbol", "Seite", "Menge", "Preis", "Gebühr EUR", "FX-Kurs", "Realisiert EUR",
            "Qualität", "Liquidität", "Während Lücke", "Quelle", "Strategie", "Marktdaten-Stand", "Order-ID", "Fill-ID");
        foreach (var f in fills.OrderBy(f => f.TimestampUtc))
        {
            b.Time(f.TimestampUtc).Text(f.Symbol).Text(Side(f.Side)).Number(f.Quantity).Number(f.Price).Number(f.Fee)
             .Number(f.FxRate).Number(f.RealizedPnl).Text(Quality(f.Quality)).Text(f.Liquidity == Liquidity.Maker ? "Maker" : "Taker")
             .Bool(f.ExecutedDuringGap).Text(Source(f.Source)).Text(f.StrategyTag)
             .Time(f.MarketDataAsOfUtc == default ? null : f.MarketDataAsOfUtc).Text(f.OrderId).Text(f.FillId).EndRow();
        }
        return b.ToString();
    }

    public static string Orders(IEnumerable<Order> orders, CsvFormat format)
    {
        var b = new CsvBuilder(format);
        b.Header("Erstellt", "Symbol", "Seite", "Typ", "Menge", "Ausgeführt", "Limit", "Stop", "Ø Preis", "Gebühren EUR",
            "Status", "Gültigkeit", "Quelle", "Client", "Strategie", "Order-ID", "Client-Order-ID");
        foreach (var o in orders.OrderBy(o => o.CreatedUtc))
        {
            b.Time(o.CreatedUtc).Text(o.Symbol).Text(Side(o.Side)).Text(o.Type.ToString()).Number(o.Quantity).Number(o.FilledQuantity)
             .Number(o.LimitPrice).Number(o.StopPrice).Number(o.FilledQuantity > 0 ? o.AveragePrice : null).Number(o.FeesPaid)
             .Text(o.Status.ToString()).Text(o.TimeInForce.ToString().ToUpperInvariant()).Text(Source(o.Source)).Text(o.ClientName)
             .Text(o.StrategyTag).Text(o.OrderId).Text(o.ClientOrderId).EndRow();
        }
        return b.ToString();
    }

    public static string Audit(IEnumerable<AuditEntry> entries, CsvFormat format)
    {
        var b = new CsvBuilder(format);
        b.Header("Zeit", "Quelle", "Client", "Aktion", "Ergebnis", "Meldung", "Freigegeben von", "Parameter", "Dauer ms");
        foreach (var e in entries.OrderBy(e => e.TimestampUtc))
        {
            b.Time(e.TimestampUtc).Text(Source(e.Source)).Text(e.ClientId).Text(e.Action).Text(e.Result).Text(e.Message)
             .Text(e.ApprovedBy).Text(e.ParametersJson).Integer(e.Duration is { } d ? (long)d.TotalMilliseconds : null).EndRow();
        }
        return b.ToString();
    }
}
