using System.Globalization;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.MarketData;
using Musterdepot.MarketData.Infrastructure;

namespace Musterdepot.MarketData.Fx;

/// <summary>Umrechnungskurs mit Herkunft.</summary>
public sealed record FxQuote(string From, string To, decimal Rate, DateTimeOffset? AsOfUtc, string Source);

/// <summary>Erweiterung von <see cref="IFxRateProvider"/>: Kurs samt Quelle und Stand abrufbar.</summary>
public interface IFxQuoteProvider : IFxRateProvider
{
    FxQuote? GetQuote(string fromCurrency, string toCurrency);
}

internal static class FxMath
{
    public const int Decimals = 10;

    /// <summary>Kurs aus "Einheiten je EUR"-Tabelle (EZB-Darstellung), auch Kreuzkurse.</summary>
    public static decimal? FromPerEur(IReadOnlyDictionary<string, decimal> perEur, string from, string to)
    {
        from = from.ToUpperInvariant();
        to = to.ToUpperInvariant();
        if (from == to) return 1m;
        decimal? f = from == "EUR" ? 1m : perEur.TryGetValue(from, out var a) ? a : null;
        decimal? t = to == "EUR" ? 1m : perEur.TryGetValue(to, out var b) ? b : null;
        if (f is not { } fv || t is not { } tv || fv == 0) return null;
        return Math.Round(tv / fv, Decimals);
    }
}

public sealed record EcbOptions
{
    public string Url { get; init; } = "https://www.ecb.europa.eu/stats/eurofxref/eurofxref-daily.xml";
    /// <summary>Nach dieser Zeit wird automatisch neu geladen (Referenzkurs erscheint werktags ca. 16:00 MEZ).</summary>
    public TimeSpan RefreshInterval { get; init; } = TimeSpan.FromHours(6);
    /// <summary>Bei Abruffehlern frühestens nach dieser Zeit erneut versuchen.</summary>
    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>Täglicher EZB-Referenzkurs (eurofxref-daily.xml). Lädt bei Bedarf selbst nach.</summary>
public sealed class EcbFxRateProvider(HttpClient http, IClock clock, EcbOptions? options = null, ILogger<EcbFxRateProvider>? logger = null) : IFxQuoteProvider
{
    private readonly EcbOptions _options = options ?? new EcbOptions();
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile EcbRates? _rates;
    private DateTimeOffset _lastAttemptUtc = DateTimeOffset.MinValue;
    private int _refreshing;

    public sealed record EcbRates(DateOnly Date, IReadOnlyDictionary<string, decimal> PerEur, DateTimeOffset FetchedUtc)
    {
        /// <summary>Referenzkurse gelten ab ca. 16:00 Uhr Berliner Zeit des Kurstags.</summary>
        public DateTimeOffset AsOfUtc => BerlinTime.ToUtc(Date.ToDateTime(new TimeOnly(16, 0)));
    }

    public EcbRates? Rates => _rates;
    public DateTimeOffset? AsOfUtc => _rates?.AsOfUtc;
    public string SourceDescription => _rates is { } r ? $"EZB-Referenzkurs vom {r.Date:dd.MM.yyyy}" : "EZB-Referenzkurs (noch nicht geladen)";

    public decimal? GetRate(string fromCurrency, string toCurrency) => GetQuote(fromCurrency, toCurrency)?.Rate;

    public FxQuote? GetQuote(string fromCurrency, string toCurrency)
    {
        MaybeRefreshInBackground();
        if (string.Equals(fromCurrency, toCurrency, StringComparison.OrdinalIgnoreCase))
            return new FxQuote(fromCurrency, toCurrency, 1m, ClockNow(), "identisch");
        var r = _rates;
        if (r is null) return null;
        var rate = FxMath.FromPerEur(r.PerEur, fromCurrency, toCurrency);
        return rate is { } v ? new FxQuote(fromCurrency.ToUpperInvariant(), toCurrency.ToUpperInvariant(), v, r.AsOfUtc, SourceDescription) : null;
    }

    private DateTimeOffset ClockNow() => clock.UtcNow;

    private void MaybeRefreshInBackground()
    {
        var now = clock.UtcNow;
        var r = _rates;
        bool stale = r is null || now - r.FetchedUtc > _options.RefreshInterval;
        if (!stale || now - _lastAttemptUtc < _options.RetryInterval) return;
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try { await RefreshAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "EZB-Kurse nicht abrufbar"); }
            finally { Interlocked.Exchange(ref _refreshing, 0); }
        });
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _lastAttemptUtc = clock.UtcNow;
            var xml = await http.GetStringAsync(_options.Url, ct).ConfigureAwait(false);
            var (date, rates) = ParseXml(xml);
            _rates = new EcbRates(date, rates, clock.UtcNow);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Kurse direkt setzen (Tests, Offline).</summary>
    public void SetRates(DateOnly date, IReadOnlyDictionary<string, decimal> perEur) => _rates = new EcbRates(date, perEur, clock.UtcNow);

    /// <summary>Liest eurofxref-daily.xml: &lt;Cube time="…"&gt;&lt;Cube currency="USD" rate="1.0812"/&gt;…</summary>
    public static (DateOnly Date, IReadOnlyDictionary<string, decimal> PerEur) ParseXml(string xml)
    {
        var doc = XDocument.Parse(xml);
        var dayCube = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Cube" && e.Attribute("time") is not null)
            ?? throw new FormatException("EZB-XML ohne Cube[@time]");
        var date = DateOnly.ParseExact(dayCube.Attribute("time")!.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var rates = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in dayCube.Elements().Where(e => e.Name.LocalName == "Cube"))
        {
            var ccy = c.Attribute("currency")?.Value;
            var rate = c.Attribute("rate")?.Value;
            if (ccy is null || rate is null) continue;
            if (decimal.TryParse(rate, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0)
                rates[ccy.ToUpperInvariant()] = v;
        }
        return (date, rates);
    }
}

public sealed record HubFxOptions
{
    /// <summary>Kurse älter als dieser Wert werden nicht genutzt.</summary>
    public TimeSpan MaxQuoteAge { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>Näherungen: Währung → Symbole (Symbol notiert in EUR: "USDC/EUR"; oder EUR in Währung: "EUR/USD", dann invertiert).</summary>
    public IReadOnlyList<(string Currency, string Symbol)> Proxies { get; init; } =
    [
        ("USD", "USDC/EUR"),
        ("USD", "EUR/USD"),
        ("USD", "USDT/EUR"),
    ];
}

/// <summary>Untertags-Näherung aus Marktkursen des Hubs (z. B. USDC/EUR bei Kraken für USD).</summary>
public sealed class HubFxRateProvider(IMarketDataHub hub, IClock clock, HubFxOptions? options = null) : IFxQuoteProvider
{
    private readonly HubFxOptions _options = options ?? new HubFxOptions();
    private FxQuote? _last;

    public DateTimeOffset? AsOfUtc => _last?.AsOfUtc;
    public string SourceDescription => _last?.Source ?? "Näherung über Marktkurse (noch kein Kurs)";
    public decimal? GetRate(string fromCurrency, string toCurrency) => GetQuote(fromCurrency, toCurrency)?.Rate;
    public Task RefreshAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Einheiten EUR je 1 Einheit <paramref name="currency"/> aus dem frischesten passenden Kurs.</summary>
    private (decimal EurPerUnit, DateTimeOffset AsOf, string Source)? EurPer(string currency)
    {
        var now = clock.UtcNow;
        foreach (var (ccy, symbol) in _options.Proxies)
        {
            if (!string.Equals(ccy, currency, StringComparison.OrdinalIgnoreCase)) continue;
            var q = hub.GetLatestQuote(symbol);
            if (q is null || q.Mid is not { } mid || mid <= 0) continue;
            if (now - q.AsOfUtc > _options.MaxQuoteAge) continue;
            if (!SymbolHelper.TrySplitPair(symbol, out var b, out var quote)) continue;
            decimal eurPerUnit = quote == "EUR" ? mid : b == "EUR" ? Math.Round(1m / mid, FxMath.Decimals) : 0m;
            if (eurPerUnit <= 0) continue;
            return (eurPerUnit, q.AsOfUtc, $"Näherung über {symbol} ({q.Source})");
        }
        return null;
    }

    public FxQuote? GetQuote(string fromCurrency, string toCurrency)
    {
        var from = fromCurrency.ToUpperInvariant();
        var to = toCurrency.ToUpperInvariant();
        if (from == to) return new FxQuote(from, to, 1m, clock.UtcNow, "identisch");
        FxQuote? q = null;
        if (to == "EUR" && EurPer(from) is { } a) q = new FxQuote(from, to, a.EurPerUnit, a.AsOf, a.Source);
        else if (from == "EUR" && EurPer(to) is { } b) q = new FxQuote(from, to, Math.Round(1m / b.EurPerUnit, FxMath.Decimals), b.AsOf, b.Source);
        if (q is not null) _last = q;
        return q;
    }
}

/// <summary>Feste Kurse (Fake-Modus, Rückfall ohne Netz).</summary>
public sealed class StaticFxRateProvider(IReadOnlyDictionary<string, decimal> perEur, string description = "feste Näherungskurse", DateTimeOffset? asOfUtc = null) : IFxQuoteProvider
{
    public static StaticFxRateProvider Default { get; } = new(new Dictionary<string, decimal>
    {
        ["USD"] = 1.08m, ["GBP"] = 0.85m, ["CHF"] = 0.95m, ["JPY"] = 165m,
    }, "feste Näherungskurse (Fake)");

    public DateTimeOffset? AsOfUtc => asOfUtc;
    public string SourceDescription => description;
    public decimal? GetRate(string fromCurrency, string toCurrency) => FxMath.FromPerEur(perEur, fromCurrency, toCurrency);
    public FxQuote? GetQuote(string fromCurrency, string toCurrency)
        => GetRate(fromCurrency, toCurrency) is { } r ? new FxQuote(fromCurrency.ToUpperInvariant(), toCurrency.ToUpperInvariant(), r, asOfUtc, description) : null;
    public Task RefreshAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Kombiniert mehrere Anbieter in Reihenfolge (Standard: Untertags-Näherung aus dem Hub, dann EZB-Referenzkurs).
/// Der erste Anbieter mit Kurs gewinnt; Kurs und Quelle sind über <see cref="GetQuote"/> abrufbar.
/// </summary>
public sealed class CompositeFxRateProvider(IReadOnlyList<IFxQuoteProvider> providers) : IFxQuoteProvider
{
    private FxQuote? _last;

    public IReadOnlyList<IFxQuoteProvider> Providers { get; } = providers;
    public FxQuote? LastQuote => _last;
    public DateTimeOffset? AsOfUtc => _last?.AsOfUtc ?? Providers.Select(p => p.AsOfUtc).Where(t => t is not null).Max();
    public string SourceDescription => _last?.Source ?? string.Join(" + ", Providers.Select(p => p.SourceDescription));

    public decimal? GetRate(string fromCurrency, string toCurrency) => GetQuote(fromCurrency, toCurrency)?.Rate;

    public FxQuote? GetQuote(string fromCurrency, string toCurrency)
    {
        foreach (var p in Providers)
        {
            var q = p.GetQuote(fromCurrency, toCurrency);
            if (q is not null) { _last = q; return q; }
        }
        return null;
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        foreach (var p in Providers)
        {
            try { await p.RefreshAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* nächster Anbieter */ _ = ex; }
        }
    }
}
