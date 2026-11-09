using Musterdepot.Core.Domain;

namespace Musterdepot.MarketData.Infrastructure;

/// <summary>Kerze plus Metadaten der zugrunde liegenden Trades (für das CandleUpdate-Ereignis).</summary>
public sealed record AggregatedCandle(Candle Candle, DataQuality Quality, int DelayedMinutes);

/// <summary>
/// Baut laufende Kerzen (M1 und höhere Zeitrahmen) aus Trades. Liefert abgeschlossene Kerzen beim
/// Periodenwechsel bzw. per <see cref="CloseDue"/> und gedrosselte Zwischenstände der offenen Kerze. Thread-sicher.
/// </summary>
public sealed class TradeCandleAggregator
{
    private sealed class Builder
    {
        public required string Symbol;
        public required Timeframe Timeframe;
        public required string Source;
        public DataQuality Quality;
        public int DelayedMinutes;
        public DateTimeOffset OpenTime;
        public decimal Open, High, Low, Close, Volume;
        public int Count;
        public DateTimeOffset LastEmitUtc;

        public Candle ToCandle(bool closed) => new()
        {
            Symbol = Symbol,
            Timeframe = Timeframe,
            OpenTimeUtc = OpenTime,
            Open = Open,
            High = High,
            Low = Low,
            Close = Close,
            Volume = Volume,
            TradeCount = Count,
            Source = Source,
            IsClosed = closed,
        };

        public AggregatedCandle ToAggregated(bool closed) => new(ToCandle(closed), Quality, DelayedMinutes);
    }

    private readonly Dictionary<(string Symbol, Timeframe Tf), Builder> _builders = new();
    private readonly Lock _lock = new();

    public TradeCandleAggregator(IReadOnlyCollection<Timeframe> timeframes, TimeSpan openCandleUpdateInterval)
    {
        Timeframes = timeframes.Distinct().OrderBy(t => t).ToArray();
        OpenCandleUpdateInterval = openCandleUpdateInterval;
    }

    public IReadOnlyList<Timeframe> Timeframes { get; }
    public TimeSpan OpenCandleUpdateInterval { get; }

    /// <summary>Verarbeitet einen Trade. Rückgabe: zu meldende Kerzen (abgeschlossene zuerst, dann offene Zwischenstände).</summary>
    public IReadOnlyList<AggregatedCandle> AddTrade(string symbol, string source, DateTimeOffset tradeTimeUtc, decimal price, decimal quantity,
        DateTimeOffset nowUtc, DataQuality quality = DataQuality.Realtime, int delayedMinutes = 0)
    {
        var result = new List<AggregatedCandle>();
        lock (_lock)
        {
            foreach (var tf in Timeframes)
            {
                var start = tf.Floor(tradeTimeUtc);
                var key = (symbol, tf);
                if (_builders.TryGetValue(key, out var b))
                {
                    if (start < b.OpenTime) continue; // verspäteter Trade für eine bereits abgeschlossene Periode
                    if (start > b.OpenTime)
                    {
                        result.Add(b.ToAggregated(true));
                        _builders.Remove(key);
                        b = null;
                    }
                }
                if (b is null)
                {
                    b = new Builder
                    {
                        Symbol = symbol, Timeframe = tf, Source = source, Quality = quality, DelayedMinutes = delayedMinutes,
                        OpenTime = start, Open = price, High = price, Low = price, Close = price, Volume = quantity, Count = 1,
                        LastEmitUtc = nowUtc,
                    };
                    _builders[key] = b;
                    result.Add(b.ToAggregated(false));
                    continue;
                }
                if (price > b.High) b.High = price;
                if (price < b.Low) b.Low = price;
                b.Close = price;
                b.Volume += quantity;
                b.Count++;
                b.Source = source;
                b.Quality = quality;
                b.DelayedMinutes = delayedMinutes;
                if (nowUtc - b.LastEmitUtc >= OpenCandleUpdateInterval)
                {
                    b.LastEmitUtc = nowUtc;
                    result.Add(b.ToAggregated(false));
                }
            }
        }
        // Abgeschlossene vor offenen, jeweils zeitlich sortiert.
        return result.OrderBy(c => c.Candle.IsClosed ? 0 : 1).ThenBy(c => c.Candle.OpenTimeUtc).ThenBy(c => c.Candle.Timeframe).ToList();
    }

    /// <summary>Schließt Kerzen, deren Periode (plus Karenz für späte Trades) vorbei ist. Bei verzögerten Quellen zählt die verzögerte Uhr.</summary>
    public IReadOnlyList<AggregatedCandle> CloseDue(DateTimeOffset nowUtc, TimeSpan grace)
    {
        var result = new List<AggregatedCandle>();
        lock (_lock)
        {
            foreach (var (key, b) in _builders.ToList())
            {
                var effectiveNow = nowUtc - TimeSpan.FromMinutes(b.DelayedMinutes);
                if (b.OpenTime + b.Timeframe.ToTimeSpan() + grace <= effectiveNow)
                {
                    result.Add(b.ToAggregated(true));
                    _builders.Remove(key);
                }
            }
        }
        return result.OrderBy(c => c.Candle.OpenTimeUtc).ThenBy(c => c.Candle.Symbol, StringComparer.Ordinal).ThenBy(c => c.Candle.Timeframe).ToList();
    }

    public Candle? GetCurrent(string symbol, Timeframe tf)
    {
        lock (_lock) return _builders.TryGetValue((symbol, tf), out var b) ? b.ToCandle(false) : null;
    }

    public void Reset(string symbol)
    {
        lock (_lock)
            foreach (var key in _builders.Keys.Where(k => k.Symbol == symbol).ToList())
                _builders.Remove(key);
    }

    // ---------- statische Helfer ----------

    /// <summary>Fasst Kerzen eines kleineren Zeitrahmens zu <paramref name="target"/> zusammen (z. B. M1 → H1).</summary>
    public static IReadOnlyList<Candle> Resample(IEnumerable<Candle> candles, Timeframe target)
    {
        return candles
            .OrderBy(c => c.OpenTimeUtc)
            .GroupBy(c => target.Floor(c.OpenTimeUtc))
            .Select(g =>
            {
                var list = g.ToList();
                return new Candle
                {
                    Symbol = list[0].Symbol,
                    Timeframe = target,
                    OpenTimeUtc = g.Key,
                    Open = list[0].Open,
                    High = list.Max(c => c.High),
                    Low = list.Min(c => c.Low),
                    Close = list[^1].Close,
                    Volume = list.Sum(c => c.Volume),
                    TradeCount = list.Sum(c => c.TradeCount),
                    Source = list[0].Source,
                    IsBackfill = list.Any(c => c.IsBackfill),
                    IsClosed = list.All(c => c.IsClosed),
                };
            })
            .ToList();
    }

    /// <summary>Baut Kerzen direkt aus einer Trade-Folge.</summary>
    public static IReadOnlyList<Candle> FromTrades(string symbol, string source, Timeframe tf,
        IEnumerable<(DateTimeOffset TimeUtc, decimal Price, decimal Quantity)> trades, DateTimeOffset? nowUtc = null)
    {
        var result = new List<Candle>();
        Candle? cur = null;
        foreach (var t in trades.OrderBy(t => t.TimeUtc))
        {
            var start = tf.Floor(t.TimeUtc);
            if (cur is not null && cur.OpenTimeUtc == start)
            {
                cur = cur with
                {
                    High = Math.Max(cur.High, t.Price),
                    Low = Math.Min(cur.Low, t.Price),
                    Close = t.Price,
                    Volume = cur.Volume + t.Quantity,
                    TradeCount = cur.TradeCount + 1,
                };
                continue;
            }
            if (cur is not null) result.Add(cur);
            cur = new Candle
            {
                Symbol = symbol, Timeframe = tf, OpenTimeUtc = start, Open = t.Price, High = t.Price, Low = t.Price, Close = t.Price,
                Volume = t.Quantity, TradeCount = 1, Source = source, IsClosed = true,
            };
        }
        if (cur is not null) result.Add(cur);
        if (nowUtc is { } now && result.Count > 0 && result[^1].OpenTimeUtc + tf.ToTimeSpan() > now)
            result[^1] = result[^1] with { IsClosed = false };
        return result;
    }
}

/// <summary>
/// Für Kerzen-Streams der Börsen (Kraken ohlc, Bitvavo candles): jede Nachricht ist der laufende Stand.
/// Beginnt eine neue Periode, wird die vorige als abgeschlossen gemeldet.
/// </summary>
public sealed class CandleStreamTracker
{
    private readonly Dictionary<(string, Timeframe), Candle> _last = new();
    private readonly Lock _lock = new();

    public IReadOnlyList<Candle> Update(Candle running)
    {
        lock (_lock)
        {
            var key = (running.Symbol, running.Timeframe);
            var result = new List<Candle>(2);
            if (_last.TryGetValue(key, out var last))
            {
                if (running.OpenTimeUtc < last.OpenTimeUtc) return result; // veraltet
                if (running.OpenTimeUtc > last.OpenTimeUtc && !last.IsClosed)
                    result.Add(last with { IsClosed = true });
            }
            _last[key] = running;
            result.Add(running);
            return result;
        }
    }

    public void Reset()
    {
        lock (_lock) _last.Clear();
    }
}
