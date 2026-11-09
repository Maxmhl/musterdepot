using Musterdepot.Core.Domain;

namespace Musterdepot.Charting;

/// <summary>
/// Technische Indikatoren, vollständig in <see cref="decimal"/> gerechnet.
/// Alle Funktionen liefern eine Liste gleicher Länge wie die Eingabe; Positionen ohne ausreichende Historie sind <c>null</c>.
/// </summary>
public static class Indicators
{
    /// <summary>Einfacher gleitender Durchschnitt.</summary>
    public static decimal?[] Sma(IReadOnlyList<decimal> values, int period)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (period < 1) throw new ArgumentOutOfRangeException(nameof(period));
        var result = new decimal?[values.Count];
        decimal sum = 0m;
        for (int i = 0; i < values.Count; i++)
        {
            sum += values[i];
            if (i >= period) sum -= values[i - period];
            if (i >= period - 1) result[i] = sum / period;
        }
        return result;
    }

    /// <summary>
    /// Exponentieller gleitender Durchschnitt (Multiplikator 2/(n+1)), Startwert = SMA der ersten n Werte
    /// (wie StockCharts/TradingView).
    /// </summary>
    public static decimal?[] Ema(IReadOnlyList<decimal> values, int period)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (period < 1) throw new ArgumentOutOfRangeException(nameof(period));
        var result = new decimal?[values.Count];
        if (values.Count < period) return result;
        decimal k = 2m / (period + 1);
        decimal seed = 0m;
        for (int i = 0; i < period; i++) seed += values[i];
        decimal ema = seed / period;
        result[period - 1] = ema;
        for (int i = period; i < values.Count; i++)
        {
            ema = (values[i] - ema) * k + ema;
            result[i] = ema;
        }
        return result;
    }

    /// <summary>
    /// Relative Strength Index nach Wilder: Start mit einfachem Durchschnitt der ersten n Gewinne/Verluste,
    /// danach Wilder-Glättung avg = (avg·(n−1) + aktuell)/n. Erster Wert an Index n.
    /// </summary>
    public static decimal?[] Rsi(IReadOnlyList<decimal> values, int period = 14)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (period < 1) throw new ArgumentOutOfRangeException(nameof(period));
        var result = new decimal?[values.Count];
        if (values.Count <= period) return result;

        decimal gain = 0m, loss = 0m;
        for (int i = 1; i <= period; i++)
        {
            var d = values[i] - values[i - 1];
            if (d > 0) gain += d; else loss -= d;
        }
        decimal avgGain = gain / period, avgLoss = loss / period;
        result[period] = RsiFrom(avgGain, avgLoss);

        for (int i = period + 1; i < values.Count; i++)
        {
            var d = values[i] - values[i - 1];
            decimal g = d > 0 ? d : 0m, l = d < 0 ? -d : 0m;
            avgGain = (avgGain * (period - 1) + g) / period;
            avgLoss = (avgLoss * (period - 1) + l) / period;
            result[i] = RsiFrom(avgGain, avgLoss);
        }
        return result;
    }

    private static decimal RsiFrom(decimal avgGain, decimal avgLoss)
    {
        if (avgLoss == 0m) return avgGain == 0m ? 50m : 100m;
        var rs = avgGain / avgLoss;
        return 100m - 100m / (1m + rs);
    }

    /// <summary>
    /// Volumengewichteter Durchschnittspreis mit typischem Preis (H+L+C)/3, kumuliert je Sitzung.
    /// Standard-Sitzung: UTC-Kalendertag. Ohne Volumen in der Sitzung bisher: <c>null</c>.
    /// </summary>
    public static decimal?[] Vwap(IReadOnlyList<Candle> candles, Func<DateTimeOffset, DateOnly>? sessionKey = null)
    {
        ArgumentNullException.ThrowIfNull(candles);
        sessionKey ??= static t => DateOnly.FromDateTime(t.UtcDateTime);
        var result = new decimal?[candles.Count];
        decimal cumPv = 0m, cumVol = 0m;
        DateOnly? session = null;
        for (int i = 0; i < candles.Count; i++)
        {
            var c = candles[i];
            var key = sessionKey(c.OpenTimeUtc);
            if (session != key)
            {
                session = key;
                cumPv = 0m;
                cumVol = 0m;
            }
            var typical = (c.High + c.Low + c.Close) / 3m;
            cumPv += typical * c.Volume;
            cumVol += c.Volume;
            result[i] = cumVol > 0m ? cumPv / cumVol : null;
        }
        return result;
    }

    /// <summary>Bollinger-Bänder: Mitte = SMA(n), Bänder = Mitte ± k·Standardabweichung (Grundgesamtheit, wie StockCharts).</summary>
    public static BollingerBands Bollinger(IReadOnlyList<decimal> values, int period = 20, decimal k = 2m)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (period < 1) throw new ArgumentOutOfRangeException(nameof(period));
        var middle = Sma(values, period);
        var upper = new decimal?[values.Count];
        var lower = new decimal?[values.Count];
        for (int i = period - 1; i < values.Count; i++)
        {
            var mean = middle[i]!.Value;
            decimal sq = 0m;
            for (int j = i - period + 1; j <= i; j++)
            {
                var d = values[j] - mean;
                sq += d * d;
            }
            var sd = Sqrt(sq / period);
            upper[i] = mean + k * sd;
            lower[i] = mean - k * sd;
        }
        return new BollingerBands(middle, upper, lower);
    }

    /// <summary>Quadratwurzel in decimal (Newton-Verfahren, ~28 Stellen).</summary>
    public static decimal Sqrt(decimal x)
    {
        if (x < 0m) throw new ArgumentOutOfRangeException(nameof(x));
        if (x == 0m) return 0m;
        decimal guess = (decimal)Math.Sqrt((double)x);
        if (guess == 0m) guess = x;
        for (int i = 0; i < 50; i++)
        {
            var next = (guess + x / guess) / 2m;
            if (next == guess) break;
            guess = next;
        }
        return guess;
    }

    public static decimal[] Closes(IReadOnlyList<Candle> candles)
    {
        var r = new decimal[candles.Count];
        for (int i = 0; i < r.Length; i++) r[i] = candles[i].Close;
        return r;
    }
}

public sealed record BollingerBands(decimal?[] Middle, decimal?[] Upper, decimal?[] Lower);
