using Musterdepot.Core.Domain;

namespace Musterdepot.Core.MarketData;

/// <summary>
/// Schnittstelle A. Jede Quelle (Kraken, Bitvavo, Coinbase, gettex, Fake, Replay) steckt hinter dieser
/// Schnittstelle und ist austauschbar.
/// </summary>
public interface IMarketDataSource : IAsyncDisposable
{
    /// <summary>Kurzname, z. B. "kraken".</summary>
    string Name { get; }
    IReadOnlyCollection<AssetClass> AssetClasses { get; }
    /// <summary>Liefert Echtzeitdaten (Krypto) oder verzögerte (Aktien).</summary>
    bool IsRealtime { get; }
    int DelayedMinutes { get; }
    ConnectionState State { get; }

    /// <summary>Wird für jedes Marktereignis ausgelöst (auf einem Hintergrund-Thread).</summary>
    event EventHandler<MarketEvent>? EventReceived;

    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);

    /// <summary>Standby: Zeit merken, sauber trennen.</summary>
    Task SuspendAsync(CancellationToken ct);
    /// <summary>Aufwachen: neu verbinden, verpasste Kerzen per REST nachladen, <see cref="GapDetected"/> melden.</summary>
    Task ResumeAsync(CancellationToken ct);

    bool Supports(string symbol);
    Task<IReadOnlyList<Instrument>> SearchSymbolsAsync(string query, CancellationToken ct);
    Task SubscribeAsync(IEnumerable<string> symbols, CancellationToken ct);
    Task UnsubscribeAsync(IEnumerable<string> symbols, CancellationToken ct);
    Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, Timeframe timeframe, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, CancellationToken ct);
    /// <summary>Nur Krypto; Quellen ohne Orderbuch liefern null.</summary>
    Task<OrderBookSnapshot?> GetOrderBookAsync(string symbol, int depth, CancellationToken ct);
}

/// <summary>Bündelt alle Quellen, routet Symbole auf Quellen, hält letzte Kurse und Orderbücher.</summary>
public interface IMarketDataHub
{
    event EventHandler<MarketEvent>? EventReceived;
    IReadOnlyList<IMarketDataSource> Sources { get; }
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    Task SuspendAsync(CancellationToken ct);
    Task ResumeAsync(CancellationToken ct);

    Task<IReadOnlyList<Instrument>> SearchSymbolsAsync(string query, CancellationToken ct);
    Instrument? GetInstrument(string symbol);
    Task SubscribeAsync(IEnumerable<string> symbols, CancellationToken ct);
    Task UnsubscribeAsync(IEnumerable<string> symbols, CancellationToken ct);
    QuoteSnapshot? GetLatestQuote(string symbol);
    IReadOnlyDictionary<string, QuoteSnapshot> GetAllQuotes();
    OrderBookSnapshot? GetLatestOrderBook(string symbol);
    Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, Timeframe timeframe, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, CancellationToken ct);
    Task<OrderBookSnapshot?> GetOrderBookAsync(string symbol, int depth, CancellationToken ct);
    IReadOnlyDictionary<string, ConnectionState> GetConnectionStates();
}

/// <summary>Währungsumrechnung (EZB-Referenzkurs täglich, untertags USDC/EUR als Näherung).</summary>
public interface IFxRateProvider
{
    /// <summary>Kurs, um 1 Einheit <paramref name="fromCurrency"/> in <paramref name="toCurrency"/> umzurechnen.</summary>
    decimal? GetRate(string fromCurrency, string toCurrency);
    DateTimeOffset? AsOfUtc { get; }
    string SourceDescription { get; }
    Task RefreshAsync(CancellationToken ct);
}

/// <summary>Handelszeiten und Feiertage (Aktien nach gettex, Krypto rund um die Uhr).</summary>
public interface ITradingCalendar
{
    bool IsOpen(Instrument instrument, DateTimeOffset utc);
    DateTimeOffset? NextOpenUtc(Instrument instrument, DateTimeOffset utc);
    /// <summary>Handelsschluss des Handelstags (für TIF=DAY), null bei 24/7.</summary>
    DateTimeOffset? SessionCloseUtc(Instrument instrument, DateTimeOffset utc);
}
