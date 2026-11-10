namespace Musterdepot.Engine.Calendar;

/// <summary>Zeitzone Europe/Berlin (Handelstag, Tagesverlust, DAY-Ablauf bei Krypto).</summary>
public static class BerlinTime
{
    public static TimeZoneInfo Zone { get; } = Find("Europe/Berlin", "W. Europe Standard Time");

    internal static TimeZoneInfo Find(params string[] ids)
    {
        foreach (var id in ids)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        // Rückfall: MEZ/MESZ nach EU-Regeln.
        var start = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday);
        var end = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1), start, end);
        return TimeZoneInfo.CreateCustomTimeZone("Europe/Berlin", TimeSpan.FromHours(1), "Europe/Berlin", "MEZ", "MESZ", [rule]);
    }

    public static DateTime ToLocal(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, Zone).DateTime;

    /// <summary>Handelstag (Kalenderdatum in Berlin).</summary>
    public static DateOnly TradingDay(DateTimeOffset utc) => DateOnly.FromDateTime(ToLocal(utc));

    /// <summary>Lokale Berliner Zeit → UTC.</summary>
    public static DateTimeOffset ToUtc(DateOnly day, TimeOnly time, TimeZoneInfo? zone = null)
    {
        zone ??= Zone;
        var local = day.ToDateTime(time, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local)) local = local.AddHours(1);
        var offset = zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    /// <summary>Nächste Mitternacht (Berlin) nach <paramref name="utc"/>, als UTC.</summary>
    public static DateTimeOffset NextMidnightUtc(DateTimeOffset utc) => ToUtc(TradingDay(utc).AddDays(1), TimeOnly.MinValue);
}
