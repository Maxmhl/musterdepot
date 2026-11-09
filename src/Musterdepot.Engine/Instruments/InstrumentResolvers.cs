using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;

namespace Musterdepot.Engine.Instruments;

/// <summary>Liefert Stammdaten (Anlageklasse, Währung, Mengenschritt) zu einem Symbol.</summary>
public interface IInstrumentResolver
{
    Instrument? Resolve(string symbol);
}

/// <summary>
/// Standard-Heuristik: Symbol mit "/" = Krypto (Quote-Währung nach dem Slash, Mengenschritt 1e-8);
/// 12-stellige ISIN = Aktie in EUR (Mengenschritt 1, gettex). Sonst unbekannt.
/// </summary>
public sealed partial class HeuristicInstrumentResolver : IInstrumentResolver
{
    public static HeuristicInstrumentResolver Instance { get; } = new();

    [GeneratedRegex("^[A-Z]{2}[A-Z0-9]{9}[0-9]$")]
    private static partial Regex IsinRegex();

    public Instrument? Resolve(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return null;
        symbol = symbol.Trim();
        var slash = symbol.IndexOf('/');
        if (slash > 0 && slash < symbol.Length - 1)
        {
            var baseAsset = symbol[..slash].ToUpperInvariant();
            var quote = symbol[(slash + 1)..].ToUpperInvariant();
            return new Instrument
            {
                Symbol = symbol,
                Name = symbol,
                AssetClass = AssetClass.Crypto,
                QuoteCurrency = quote,
                BaseAsset = baseAsset,
                TickSize = 0.00000001m,
                QuantityStep = 0.00000001m,
                MinQuantity = 0.00000001m,
            };
        }
        if (symbol.Length == 12 && IsinRegex().IsMatch(symbol))
        {
            return new Instrument
            {
                Symbol = symbol,
                Name = symbol,
                AssetClass = AssetClass.Stock,
                QuoteCurrency = "EUR",
                Isin = symbol,
                TickSize = 0.001m,
                QuantityStep = 1m,
                MinQuantity = 1m,
                PrimarySource = "gettex",
            };
        }
        return null;
    }
}

/// <summary>Feste Instrumentliste (Tests, Backtests) mit optionalem Rückfall.</summary>
public sealed class StaticInstrumentResolver : IInstrumentResolver
{
    private readonly ConcurrentDictionary<string, Instrument> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly IInstrumentResolver? _fallback;

    public StaticInstrumentResolver(IEnumerable<Instrument> instruments, IInstrumentResolver? fallback = null)
    {
        foreach (var i in instruments) _map[i.Symbol] = i;
        _fallback = fallback;
    }

    public void Register(Instrument instrument) => _map[instrument.Symbol] = instrument;

    public Instrument? Resolve(string symbol) => _map.TryGetValue(symbol, out var i) ? i : _fallback?.Resolve(symbol);
}

/// <summary>Adapter auf <see cref="IMarketDataHub.GetInstrument"/>, mit Rückfall auf die Heuristik.</summary>
public sealed class MarketDataHubInstrumentResolver : IInstrumentResolver
{
    private readonly Func<IMarketDataHub?> _hub;
    private readonly IInstrumentResolver _fallback;

    public MarketDataHubInstrumentResolver(IMarketDataHub hub, IInstrumentResolver? fallback = null)
        : this(() => hub, fallback) { }

    /// <summary>Hub wird verzögert aufgelöst (vermeidet Zyklen im DI-Container).</summary>
    public MarketDataHubInstrumentResolver(Func<IMarketDataHub?> hub, IInstrumentResolver? fallback = null)
    {
        _hub = hub;
        _fallback = fallback ?? HeuristicInstrumentResolver.Instance;
    }

    public Instrument? Resolve(string symbol)
    {
        Instrument? fromHub = null;
        try { fromHub = _hub()?.GetInstrument(symbol); } catch { /* Hub nicht bereit → Heuristik */ }
        return fromHub ?? _fallback.Resolve(symbol);
    }
}
