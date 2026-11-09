namespace Musterdepot.MarketData.Infrastructure;

/// <summary>
/// Umrechnung Europe/Berlin ↔ UTC (gettex-Zeiten, EZB-Kurszeitpunkt). Nutzt die System-Zeitzonendaten
/// (IANA oder Windows-ID) und fällt sonst auf die EU-Sommerzeitregel zurück.
/// </summary>
public static class BerlinTime
{
    private static readonly TimeZoneInfo? Zone = FindZone();

    private static TimeZoneInfo? FindZone()
    {
        foreach (var id in new[] { "Europe/Berlin", "W. Europe Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return null;
    }

    /// <summary>Lokale Berliner Zeit → UTC. Mehrdeutige Zeiten (Zeitumstellung) gelten als Normalzeit.</summary>
    public static DateTimeOffset ToUtc(DateTime berlinLocal)
    {
        var local = DateTime.SpecifyKind(berlinLocal, DateTimeKind.Unspecified);
        var offset = GetOffsetForLocal(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    /// <summary>UTC → lokale Berliner Zeit (Kind = Unspecified).</summary>
    public static DateTime FromUtc(DateTimeOffset utc)
    {
        var u = utc.UtcDateTime;
        var offset = Zone is not null ? Zone.GetUtcOffset(u) : (IsSummerTimeUtc(u) ? TimeSpan.FromHours(2) : TimeSpan.FromHours(1));
        return DateTime.SpecifyKind(u + offset, DateTimeKind.Unspecified);
    }

    public static TimeSpan GetOffsetForLocal(DateTime local)
    {
        if (Zone is not null) return Zone.GetUtcOffset(local);
        // Rückfall: EU-Regel, Umstellung 01:00 UTC am letzten Sonntag im März/Oktober.
        var asStandardUtc = local - TimeSpan.FromHours(1);
        return IsSummerTimeUtc(asStandardUtc) ? TimeSpan.FromHours(2) : TimeSpan.FromHours(1);
    }

    private static bool IsSummerTimeUtc(DateTime utc)
    {
        var start = LastSunday(utc.Year, 3).AddHours(1);
        var end = LastSunday(utc.Year, 10).AddHours(1);
        return utc >= start && utc < end;
    }

    private static DateTime LastSunday(int year, int month)
    {
        var d = new DateTime(year, month, DateTime.DaysInMonth(year, month));
        while (d.DayOfWeek != DayOfWeek.Sunday) d = d.AddDays(-1);
        return d;
    }
}
