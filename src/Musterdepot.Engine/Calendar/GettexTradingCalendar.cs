using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;

namespace Musterdepot.Engine.Calendar;

/// <summary>Feiertagsregeln für den Aktienhandel (gettex-Annahme).</summary>
[Flags]
public enum ExchangeHoliday
{
    None = 0,
    NewYear = 1,
    GoodFriday = 2,
    EasterMonday = 4,
    LabourDay = 8,
    ChristmasEve = 16,
    ChristmasDay = 32,
    BoxingDay = 64,
    NewYearsEve = 128,
    All = NewYear | GoodFriday | EasterMonday | LabourDay | ChristmasEve | ChristmasDay | BoxingDay | NewYearsEve,
}

/// <summary>
/// Konfigurierbare Annahme für gettex: Mo–Fr 07:30–23:00 Europe/Berlin, geschlossen an Neujahr, Karfreitag,
/// Ostermontag, 1. Mai, 24.–26.12. und 31.12.
/// </summary>
public sealed record GettexCalendarOptions
{
    public string TimeZoneId { get; init; } = "Europe/Berlin";
    public TimeOnly OpenTime { get; init; } = new(7, 30);
    public TimeOnly CloseTime { get; init; } = new(23, 0);
    public ExchangeHoliday Holidays { get; init; } = ExchangeHoliday.All;
    /// <summary>Zusätzliche Schließtage (lokales Datum).</summary>
    public IReadOnlyList<DateOnly> AdditionalClosedDays { get; init; } = [];
    /// <summary>Anlageklassen, die rund um die Uhr handelbar sind.</summary>
    public IReadOnlyList<AssetClass> AlwaysOpenAssetClasses { get; init; } = [AssetClass.Crypto, AssetClass.Fx];
}

/// <summary>Handelszeiten: Aktien/ETFs nach gettex-Annahme mit Feiertagskalender, Krypto 24/7.</summary>
public sealed class GettexTradingCalendar : ITradingCalendar
{
    private readonly GettexCalendarOptions _o;
    private readonly TimeZoneInfo _zone;

    public GettexTradingCalendar() : this(null) { }

    public GettexTradingCalendar(GettexCalendarOptions? options)
    {
        _o = options ?? new GettexCalendarOptions();
        _zone = _o.TimeZoneId == "Europe/Berlin" ? BerlinTime.Zone : BerlinTime.Find(_o.TimeZoneId);
    }

    public GettexCalendarOptions Options => _o;

    private bool AlwaysOpen(Instrument instrument) => _o.AlwaysOpenAssetClasses.Contains(instrument.AssetClass);

    public bool IsOpen(Instrument instrument, DateTimeOffset utc)
    {
        if (AlwaysOpen(instrument)) return true;
        var local = TimeZoneInfo.ConvertTime(utc, _zone);
        var day = DateOnly.FromDateTime(local.DateTime);
        if (!IsTradingDay(day)) return false;
        var t = TimeOnly.FromDateTime(local.DateTime);
        return t >= _o.OpenTime && t < _o.CloseTime;
    }

    public DateTimeOffset? NextOpenUtc(Instrument instrument, DateTimeOffset utc)
    {
        if (AlwaysOpen(instrument) || IsOpen(instrument, utc)) return utc;
        var local = TimeZoneInfo.ConvertTime(utc, _zone);
        var day = DateOnly.FromDateTime(local.DateTime);
        var t = TimeOnly.FromDateTime(local.DateTime);
        if (IsTradingDay(day) && t < _o.OpenTime) return BerlinTime.ToUtc(day, _o.OpenTime, _zone);
        for (int i = 1; i <= 30; i++)
        {
            var d = day.AddDays(i);
            if (IsTradingDay(d)) return BerlinTime.ToUtc(d, _o.OpenTime, _zone);
        }
        return null;
    }

    public DateTimeOffset? SessionCloseUtc(Instrument instrument, DateTimeOffset utc)
    {
        if (AlwaysOpen(instrument)) return null;
        var open = NextOpenUtc(instrument, utc);
        if (open is null) return null;
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(open.Value, _zone).DateTime);
        return BerlinTime.ToUtc(day, _o.CloseTime, _zone);
    }

    public bool IsTradingDay(DateOnly day)
    {
        if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
        if (_o.AdditionalClosedDays.Contains(day)) return false;
        return !IsHoliday(day, _o.Holidays);
    }

    public static bool IsHoliday(DateOnly day, ExchangeHoliday rules = ExchangeHoliday.All)
    {
        var easter = EasterSunday(day.Year);
        return (rules.HasFlag(ExchangeHoliday.NewYear) && day.Month == 1 && day.Day == 1)
            || (rules.HasFlag(ExchangeHoliday.GoodFriday) && day == easter.AddDays(-2))
            || (rules.HasFlag(ExchangeHoliday.EasterMonday) && day == easter.AddDays(1))
            || (rules.HasFlag(ExchangeHoliday.LabourDay) && day.Month == 5 && day.Day == 1)
            || (rules.HasFlag(ExchangeHoliday.ChristmasEve) && day.Month == 12 && day.Day == 24)
            || (rules.HasFlag(ExchangeHoliday.ChristmasDay) && day.Month == 12 && day.Day == 25)
            || (rules.HasFlag(ExchangeHoliday.BoxingDay) && day.Month == 12 && day.Day == 26)
            || (rules.HasFlag(ExchangeHoliday.NewYearsEve) && day.Month == 12 && day.Day == 31);
    }

    /// <summary>Ostersonntag (gregorianisch, anonymer Algorithmus nach Meeus/Jones/Butcher).</summary>
    public static DateOnly EasterSunday(int year)
    {
        int a = year % 19, b = year / 100, c = year % 100, d = b / 4, e = b % 4;
        int f = (b + 8) / 25, g = (b - f + 1) / 3, h = (19 * a + b - d - g + 15) % 30;
        int i = c / 4, k = c % 4, l = (32 + 2 * e + 2 * i - h - k) % 7, m = (a + 11 * h + 22 * l) / 451;
        int month = (h + l - 7 * m + 114) / 31, day = (h + l - 7 * m + 114) % 31 + 1;
        return new DateOnly(year, month, day);
    }
}
