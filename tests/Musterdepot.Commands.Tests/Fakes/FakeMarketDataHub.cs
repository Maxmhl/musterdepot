using System.Collections.Concurrent;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;

namespace Musterdepot.Commands.Tests.Fakes;

public sealed class FakeMarketDataHub : IMarketDataHub
{
    private readonly ConcurrentDictionary<string, QuoteSnapshot> _quotes = new();
    private readonly ConcurrentDictionary<string, ConnectionState> _states = new();

    public event EventHandler<MarketEvent>? EventReceived;
    public IReadOnlyList<IMarketDataSource> Sources => [];
    public ConcurrentQueue<string> Subscribed { get; } = new();
    public List<Instrument> Instruments { get; } = [];
    public List<Candle> Candles { get; } = [];
    public bool Started { get; private set; }
    public bool Stopped { get; private set; }
    public bool Suspended { get; private set; }
    public bool Resumed { get; private set; }

    public Task StartAsync(CancellationToken ct) { Started = true; return Task.CompletedTask; }
    public Task StopAsync(CancellationToken ct) { Stopped = true; return Task.CompletedTask; }
    public Task SuspendAsync(CancellationToken ct) { Suspended = true; return Task.CompletedTask; }
    public Task ResumeAsync(CancellationToken ct) { Resumed = true; return Task.CompletedTask; }

    public Task<IReadOnlyList<Instrument>> SearchSymbolsAsync(string query, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<Instrument>>(Instruments.Where(i => i.Symbol.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList());

    public Instrument? GetInstrument(string symbol) => Instruments.FirstOrDefault(i => i.Symbol == symbol);

    public Task SubscribeAsync(IEnumerable<string> symbols, CancellationToken ct)
    {
        foreach (var s in symbols) Subscribed.Enqueue(s);
        return Task.CompletedTask;
    }

    public Task UnsubscribeAsync(IEnumerable<string> symbols, CancellationToken ct) => Task.CompletedTask;
    public QuoteSnapshot? GetLatestQuote(string symbol) => _quotes.GetValueOrDefault(symbol);
    public IReadOnlyDictionary<string, QuoteSnapshot> GetAllQuotes() => new Dictionary<string, QuoteSnapshot>(_quotes);
    public OrderBookSnapshot? GetLatestOrderBook(string symbol) => null;

    public Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, Timeframe timeframe, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<Candle>>(Candles.Where(c => c.Symbol == symbol && c.Timeframe == timeframe).ToList());

    public Task<OrderBookSnapshot?> GetOrderBookAsync(string symbol, int depth, CancellationToken ct) => Task.FromResult<OrderBookSnapshot?>(null);
    public IReadOnlyDictionary<string, ConnectionState> GetConnectionStates() => new Dictionary<string, ConnectionState>(_states);

    /// <summary>Ereignis einspeisen (aktualisiert Kurse/Verbindungsstatus wie der echte Hub).</summary>
    public void Publish(MarketEvent e)
    {
        switch (e)
        {
            case QuoteTick q:
                _quotes[q.Symbol] = new QuoteSnapshot { Symbol = q.Symbol, Source = q.Source, AsOfUtc = q.AsOfUtc, Bid = q.Bid, Ask = q.Ask, Last = q.Last, ChangePercent = q.ChangePercent, IsRealtime = q.IsRealtime };
                break;
            case TradeTick t:
                _quotes[t.Symbol] = (_quotes.GetValueOrDefault(t.Symbol) ?? new QuoteSnapshot { Symbol = t.Symbol, Source = t.Source, AsOfUtc = t.AsOfUtc }) with { Last = t.Price, AsOfUtc = t.AsOfUtc };
                break;
            case ConnectionStatusChanged c:
                _states[c.Source] = c.State;
                break;
        }
        EventReceived?.Invoke(this, e);
    }

    public static QuoteTick Quote(string symbol, decimal last, decimal? changePercent = null) => new()
    {
        Symbol = symbol, Source = "fake", AsOfUtc = DateTimeOffset.UtcNow, Last = last, Bid = last - 1, Ask = last + 1, ChangePercent = changePercent,
    };
}

public sealed class RecordingNotifier : IUserNotifier
{
    public ConcurrentQueue<(string Title, string Message)> Notifications { get; } = new();
    public ConcurrentQueue<ApprovalRequest> ApprovalRequests { get; } = new();
    public void Notify(string title, string message) => Notifications.Enqueue((title, message));
    public void NotifyApprovalRequested(ApprovalRequest request) => ApprovalRequests.Enqueue(request);
}
