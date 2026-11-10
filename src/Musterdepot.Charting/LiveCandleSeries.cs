using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;

namespace Musterdepot.Charting;

/// <summary>
/// Hält die Kerzen eines Symbols in einem Zeitrahmen und führt Live-Ticks nach.
/// Nicht thread-sicher – vom Aufrufer (UI-Thread) serialisiert verwenden.
/// </summary>
public sealed class LiveCandleSeries
{
    private readonly List<Candle> _candles;

    public LiveCandleSeries(string symbol, Timeframe timeframe, IEnumerable<Candle> history, int maxCandles = 2000)
    {
        Symbol = symbol;
        Timeframe = timeframe;
        MaxCandles = maxCandles;
        _candles = [.. ChartMapper.Normalize(history)];
        Trim();
    }

    public string Symbol { get; }
    public Timeframe Timeframe { get; }
    public int MaxCandles { get; }
    public IReadOnlyList<Candle> Candles => _candles;

    /// <summary>Wendet ein Marktereignis an. Liefert die geänderte (letzte) Kerze oder <c>null</c>, wenn nichts passiert ist.</summary>
    public Candle? Apply(MarketEvent e)
    {
        if (!string.Equals(e.Symbol, Symbol, StringComparison.OrdinalIgnoreCase)) return null;
        return e switch
        {
            TradeTick t => ApplyPrice(t.AsOfUtc, t.Price, t.Quantity, e.Source),
            QuoteTick q when q.Last is { } last => ApplyPrice(q.AsOfUtc, last, 0m, e.Source),
            CandleUpdate cu when cu.Candle.Timeframe == Timeframe => ApplyCandle(cu.Candle),
            _ => null,
        };
    }

    /// <summary>Fügt nachgeladene Kerzen (z. B. nach einer Lücke) ein.</summary>
    public void Merge(IEnumerable<Candle> candles)
    {
        var all = _candles.Concat(candles.Where(c => c.Timeframe == Timeframe));
        var normalized = ChartMapper.Normalize(all);
        _candles.Clear();
        _candles.AddRange(normalized);
        Trim();
    }

    private Candle? ApplyCandle(Candle c)
    {
        if (_candles.Count == 0 || c.OpenTimeUtc > _candles[^1].OpenTimeUtc)
        {
            _candles.Add(c);
            Trim();
            return c;
        }
        if (c.OpenTimeUtc == _candles[^1].OpenTimeUtc)
        {
            _candles[^1] = c;
            return c;
        }
        return null; // ältere Kerze: im Live-Strom ignorieren
    }

    private Candle? ApplyPrice(DateTimeOffset asOf, decimal price, decimal quantity, string source)
    {
        if (price <= 0m) return null;
        var open = Timeframe.Floor(asOf);
        if (_candles.Count > 0)
        {
            var last = _candles[^1];
            if (open < last.OpenTimeUtc) return null;
            if (open == last.OpenTimeUtc)
            {
                var updated = last with
                {
                    High = Math.Max(last.High, price),
                    Low = Math.Min(last.Low, price),
                    Close = price,
                    Volume = last.Volume + quantity,
                    TradeCount = last.TradeCount + (quantity > 0m ? 1 : 0),
                    IsClosed = false,
                };
                _candles[^1] = updated;
                return updated;
            }
        }
        var candle = new Candle
        {
            Symbol = Symbol,
            Timeframe = Timeframe,
            OpenTimeUtc = open,
            Open = price,
            High = price,
            Low = price,
            Close = price,
            Volume = quantity,
            TradeCount = quantity > 0m ? 1 : 0,
            Source = source,
            IsClosed = false,
        };
        _candles.Add(candle);
        Trim();
        return candle;
    }

    private void Trim()
    {
        if (_candles.Count > MaxCandles) _candles.RemoveRange(0, _candles.Count - MaxCandles);
    }
}
