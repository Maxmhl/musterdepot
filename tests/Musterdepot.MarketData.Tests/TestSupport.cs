using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;
using Musterdepot.MarketData.Sources;

namespace Musterdepot.MarketData.Tests;

internal static class Fixture
{
    public static string Path(string relative) => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", relative);
    public static string Read(string relative) => File.ReadAllText(Path(relative));
    public static byte[] Bytes(string relative) => File.ReadAllBytes(Path(relative));
}

internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, int timeoutMs = 5000, string? because = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Bedingung nicht erfüllt: " + because);
            await Task.Delay(10);
        }
    }
}

/// <summary>Sammelt Ereignisse thread-sicher.</summary>
internal sealed class EventLog
{
    private readonly ConcurrentQueue<MarketEvent> _events = new();
    public void Add(object? sender, MarketEvent e) => _events.Enqueue(e);
    public IReadOnlyList<MarketEvent> All => _events.ToArray();
    public IReadOnlyList<T> Of<T>() where T : MarketEvent => _events.OfType<T>().ToList();
}

/// <summary>WebSocket-Attrappe: Tests schieben Nachrichten hinein und lesen gesendete Nachrichten.</summary>
internal sealed class FakeWebSocket : IWebSocketConnection
{
    private readonly Channel<string?> _incoming = Channel.CreateUnbounded<string?>();
    private readonly ConcurrentQueue<string> _sent = new();
    public bool FailConnect { get; init; }
    public bool IsOpen { get; private set; }
    public bool Disposed { get; private set; }
    public IReadOnlyList<string> Sent => _sent.ToArray();

    public Task ConnectAsync(Uri uri, CancellationToken ct)
    {
        if (FailConnect) throw new WebSocketLikeException("Verbindung verweigert");
        IsOpen = true;
        return Task.CompletedTask;
    }

    public Task SendAsync(string message, CancellationToken ct)
    {
        _sent.Enqueue(message);
        return Task.CompletedTask;
    }

    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        var msg = await _incoming.Reader.ReadAsync(ct);
        if (msg is null) IsOpen = false;
        return msg;
    }

    public void Push(string message) => _incoming.Writer.TryWrite(message);
    public void ServerClose() => _incoming.Writer.TryWrite(null);

    public Task CloseAsync(CancellationToken ct)
    {
        IsOpen = false;
        _incoming.Writer.TryWrite(null);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        IsOpen = false;
        return ValueTask.CompletedTask;
    }
}

internal sealed class WebSocketLikeException(string message) : Exception(message);

internal sealed class FakeWebSocketFactory : IWebSocketConnectionFactory
{
    private readonly ConcurrentQueue<FakeWebSocket> _planned = new();
    private readonly ConcurrentQueue<FakeWebSocket> _created = new();
    public void Plan(FakeWebSocket ws) => _planned.Enqueue(ws);
    public IReadOnlyList<FakeWebSocket> Created => _created.ToArray();
    public FakeWebSocket Last => _created.Last();

    public IWebSocketConnection Create()
    {
        var ws = _planned.TryDequeue(out var p) ? p : new FakeWebSocket();
        _created.Enqueue(ws);
        return ws;
    }
}

/// <summary>HttpMessageHandler-Attrappe: Antwort je Anfrage per Funktion.</summary>
internal sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    private readonly ConcurrentQueue<Uri> _requests = new();
    public IReadOnlyList<Uri> Requests => _requests.ToArray();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _requests.Enqueue(request.RequestUri!);
        return Task.FromResult(respond(request));
    }

    public static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
    public static HttpResponseMessage Bytes(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
    public static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound);
    public static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond, out FakeHttpHandler handler)
    {
        handler = new FakeHttpHandler(respond);
        return new HttpClient(handler);
    }
}

/// <summary>Steuerbare Quelle für Hub-Tests (Routing, Failover).</summary>
internal sealed class TestSource(string name, Func<string, bool> supports, params AssetClass[] classes) : IMarketDataSource
{
    private ConnectionState _state = ConnectionState.Disconnected;
    public string Name { get; } = name;
    public IReadOnlyCollection<AssetClass> AssetClasses { get; } = classes.Length > 0 ? classes : [AssetClass.Crypto];
    public bool IsRealtime => DelayedMinutes == 0;
    public int DelayedMinutes { get; init; }
    public ConnectionState State => _state;
    public event EventHandler<MarketEvent>? EventReceived;
    public HashSet<string> Subscribed { get; } = new();
    public List<(DateTimeOffset? From, DateTimeOffset? To, Timeframe Tf)> CandleRequests { get; } = new();
    public Func<string, IReadOnlyList<Candle>> Candles { get; set; } = _ => [];
    public TimeSpan? MaxLookback { get; init; }
    public int SuspendCalls, ResumeCalls;

    public void SetState(ConnectionState state, DateTimeOffset now)
    {
        _state = state;
        Emit(new ConnectionStatusChanged { Symbol = "*", Source = Name, AsOfUtc = now, State = state });
    }

    public void Emit(MarketEvent e) => EventReceived?.Invoke(this, e);

    public Task StartAsync(CancellationToken ct) { _state = ConnectionState.Connected; return Task.CompletedTask; }
    public Task StopAsync(CancellationToken ct) { _state = ConnectionState.Disconnected; return Task.CompletedTask; }
    public Task SuspendAsync(CancellationToken ct) { SuspendCalls++; _state = ConnectionState.Suspended; return Task.CompletedTask; }
    public Task ResumeAsync(CancellationToken ct) { ResumeCalls++; _state = ConnectionState.Connected; return Task.CompletedTask; }
    public bool Supports(string symbol) => supports(symbol);
    public Task<IReadOnlyList<Instrument>> SearchSymbolsAsync(string query, CancellationToken ct) => Task.FromResult<IReadOnlyList<Instrument>>([]);
    public Task SubscribeAsync(IEnumerable<string> symbols, CancellationToken ct) { lock (Subscribed) foreach (var s in symbols) Subscribed.Add(s); return Task.CompletedTask; }
    public Task UnsubscribeAsync(IEnumerable<string> symbols, CancellationToken ct) { lock (Subscribed) foreach (var s in symbols) Subscribed.Remove(s); return Task.CompletedTask; }

    public Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, Timeframe timeframe, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, CancellationToken ct)
    {
        CandleRequests.Add((fromUtc, toUtc, timeframe));
        return Task.FromResult(Candles(symbol));
    }

    public Task<OrderBookSnapshot?> GetOrderBookAsync(string symbol, int depth, CancellationToken ct) => Task.FromResult<OrderBookSnapshot?>(null);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class HistoryTestSource(string name, Func<string, bool> supports, TimeSpan? lookback) : IMarketDataSource, IHistoryCapabilities
{
    private readonly TestSource _inner = new(name, supports);
    public List<DateTimeOffset?> Requests { get; } = new();
    public TimeSpan? GetMaxHistoryLookback(Timeframe timeframe) => lookback;
    public string Name => _inner.Name;
    public IReadOnlyCollection<AssetClass> AssetClasses => _inner.AssetClasses;
    public bool IsRealtime => true;
    public int DelayedMinutes => 0;
    public ConnectionState State => _inner.State;
    public event EventHandler<MarketEvent>? EventReceived { add { } remove { } }
    public Task StartAsync(CancellationToken ct) => _inner.StartAsync(ct);
    public Task StopAsync(CancellationToken ct) => _inner.StopAsync(ct);
    public Task SuspendAsync(CancellationToken ct) => _inner.SuspendAsync(ct);
    public Task ResumeAsync(CancellationToken ct) => _inner.ResumeAsync(ct);
    public bool Supports(string symbol) => _inner.Supports(symbol);
    public Task<IReadOnlyList<Instrument>> SearchSymbolsAsync(string query, CancellationToken ct) => _inner.SearchSymbolsAsync(query, ct);
    public Task SubscribeAsync(IEnumerable<string> symbols, CancellationToken ct) => _inner.SubscribeAsync(symbols, ct);
    public Task UnsubscribeAsync(IEnumerable<string> symbols, CancellationToken ct) => _inner.UnsubscribeAsync(symbols, ct);
    public Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, Timeframe timeframe, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, CancellationToken ct)
    {
        Requests.Add(fromUtc);
        IReadOnlyList<Candle> list = [new Candle { Symbol = symbol, Timeframe = timeframe, OpenTimeUtc = fromUtc ?? DateTimeOffset.UnixEpoch, Open = 1, High = 1, Low = 1, Close = 1, Source = Name }];
        return Task.FromResult(list);
    }
    public Task<OrderBookSnapshot?> GetOrderBookAsync(string symbol, int depth, CancellationToken ct) => _inner.GetOrderBookAsync(symbol, depth, ct);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
