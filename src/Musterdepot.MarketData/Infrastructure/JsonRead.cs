using System.Globalization;
using System.Text.Json;

namespace Musterdepot.MarketData.Infrastructure;

/// <summary>Hilfen zum verlustfreien Lesen von Zahlen und Zeitstempeln aus Börsen-JSON (Zahlen oder Strings).</summary>
internal static class JsonRead
{
    public static decimal Dec(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Number:
                if (e.TryGetDecimal(out var d)) return d;
                return decimal.Parse(e.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture);
            case JsonValueKind.String:
                return decimal.Parse(e.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture);
            default:
                throw new FormatException($"Kein Zahlenwert: {e.ValueKind}");
        }
    }

    public static decimal? DecOrNull(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Number) return Dec(e);
        if (e.ValueKind == JsonValueKind.String)
        {
            var s = e.GetString();
            if (string.IsNullOrWhiteSpace(s)) return null;
            return decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        }
        return null;
    }

    public static decimal? DecOrNull(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var e) ? DecOrNull(e) : null;

    public static string? Str(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var e)) return null;
        return e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    public static long? Long(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var e)) return null;
        if (e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var l)) return l;
        if (e.ValueKind == JsonValueKind.String && long.TryParse(e.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) return l;
        return null;
    }

    public static bool? Bool(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var e)) return null;
        return e.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    }

    public static DateTimeOffset? Time(JsonElement parent, string name)
    {
        var s = Str(parent, name);
        return s is null ? null : TryParseIsoUtc(s, out var t) ? t : null;
    }

    /// <summary>ISO-8601/RFC-3339 mit bis zu 9 Nachkommastellen (Kraken sendet Nanosekunden).</summary>
    public static bool TryParseIsoUtc(string s, out DateTimeOffset result)
    {
        s = s.Trim();
        var tIndex = s.IndexOf('T');
        var dot = s.IndexOf('.', tIndex >= 0 ? tIndex : 0);
        if (dot > 0)
        {
            int end = dot + 1;
            while (end < s.Length && char.IsAsciiDigit(s[end])) end++;
            int digits = end - dot - 1;
            if (digits > 7) s = s[..(dot + 8)] + s[end..];
        }
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out result))
        {
            result = result.ToUniversalTime();
            return true;
        }
        return false;
    }

    public static DateTimeOffset FromUnixMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);
    public static DateTimeOffset FromUnixSeconds(long s) => DateTimeOffset.FromUnixTimeSeconds(s);

    /// <summary>Unix-Sekunden, auch mit Nachkommastellen (z. B. "1688671200.1234").</summary>
    public static DateTimeOffset FromUnixSecondsDecimal(decimal seconds)
    {
        var ticks = (long)(seconds * TimeSpan.TicksPerSecond);
        return DateTimeOffset.UnixEpoch.AddTicks(ticks);
    }
}
