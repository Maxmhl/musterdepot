using System.Globalization;
using Musterdepot.Core.MarketData;

namespace Musterdepot.Charting;

/// <summary>Anzeigeformate für die Oberfläche (plattformneutral, damit testbar).</summary>
public static class DisplayFormat
{
    /// <summary>Preis mit an die Größenordnung angepassten Nachkommastellen.</summary>
    public static string Price(decimal? value, CultureInfo? culture = null)
    {
        if (value is not { } v) return "–";
        culture ??= CultureInfo.CurrentCulture;
        var a = Math.Abs(v);
        var decimals = a switch
        {
            0m => 2,
            >= 1m => 2,
            >= 0.1m => 4,
            >= 0.001m => 6,
            _ => 8,
        };
        return v.ToString("N" + decimals, culture);
    }

    public static string Money(decimal? value, string currency = "EUR", CultureInfo? culture = null)
    {
        if (value is not { } v) return "–";
        culture ??= CultureInfo.CurrentCulture;
        var symbol = currency == "EUR" ? "€" : currency;
        return v.ToString("N2", culture) + " " + symbol;
    }

    public static string SignedMoney(decimal? value, string currency = "EUR", CultureInfo? culture = null)
    {
        if (value is not { } v) return "–";
        var s = Money(Math.Abs(v), currency, culture);
        return v > 0 ? "+" + s : v < 0 ? "−" + s : s;
    }

    public static string Percent(decimal? value, CultureInfo? culture = null)
    {
        if (value is not { } v) return "–";
        culture ??= CultureInfo.CurrentCulture;
        var s = Math.Abs(v).ToString("N2", culture) + " %";
        return v > 0 ? "+" + s : v < 0 ? "−" + s : s;
    }

    /// <summary>Menge ohne überflüssige Nullen (Krypto bis 8 Stellen).</summary>
    public static string Quantity(decimal? value, CultureInfo? culture = null)
    {
        if (value is not { } v) return "–";
        culture ??= CultureInfo.CurrentCulture;
        return v.ToString("#,##0.########", culture);
    }

    /// <summary>Text der Verzögerungsmarke, z. B. "verzögert 15 Min" – <c>null</c> bei Echtzeit.</summary>
    public static string? DelayBadge(QuoteSnapshot? quote)
    {
        if (quote is null) return null;
        if (quote.DelayedMinutes > 0) return $"verzögert {quote.DelayedMinutes} Min";
        if (!quote.IsRealtime) return quote.Quality switch
        {
            Core.Domain.DataQuality.Replay => "Replay",
            Core.Domain.DataQuality.Synthetic => "synthetisch",
            Core.Domain.DataQuality.Backfill => "nachgeladen",
            _ => "verzögert",
        };
        return null;
    }

    /// <summary>Alter eines Datenstands in Worten, z. B. "vor 3 Min".</summary>
    public static string Age(DateTimeOffset asOf, DateTimeOffset now)
    {
        var d = now - asOf;
        if (d < TimeSpan.FromSeconds(5)) return "gerade eben";
        if (d < TimeSpan.FromMinutes(1)) return $"vor {(int)d.TotalSeconds} s";
        if (d < TimeSpan.FromHours(1)) return $"vor {(int)d.TotalMinutes} Min";
        if (d < TimeSpan.FromDays(1)) return $"vor {(int)d.TotalHours} Std";
        return $"vor {(int)d.TotalDays} Tg";
    }
}
