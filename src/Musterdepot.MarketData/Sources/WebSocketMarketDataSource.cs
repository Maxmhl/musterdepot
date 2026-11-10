using Microsoft.Extensions.Logging;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;
using Musterdepot.MarketData.Infrastructure;

namespace Musterdepot.MarketData.Sources;

public enum WsMessageKind { Data, Heartbeat, Pong, SubscriptionAck, Status, Error, Ignored }

/// <summary>Ergebnis eines Parser-Aufrufs für eine WebSocket-Nachricht.</summary>
public sealed record ParsedWsMessage(WsMessageKind Kind, IReadOnlyList<MarketEvent> Events)
{
    public string? Message { get; init; }
    public string? Channel { get; init; }
    /// <summary>Folgenummer (z. B. Bitvavo-Orderbuch-Nonce), falls vorhanden.</summary>
    public long? Sequence { get; init; }

    public static ParsedWsMessage Of(WsMessageKind kind, string? message = null) => new(kind, []) { Message = message };
}

/// <summary>Gemeinsame Einstellungen für WebSocket-Quellen.</summary>
public record WebSocketSourceOptions
{
    /// <summary>Kommt so lange nichts an (trotz Heartbeat/Ping), gilt die Verbindung als tot.</summary>
    public TimeSpan ReceiveTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Abstand für anwendungsseitige Pings (falls die Quelle das unterstützt).</summary>
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public BackoffOptions Backoff { get; init; } = new();
    /// <summary>Nach so vielen Fehlversuchen in Folge Status Failed (0 = unbegrenzt weiter versuchen).</summary>
    public int MaxConsecutiveFailures { get; init; }
    /// <summary>Verbindungsversuche je Fenster (eigener Limiter, wenn keiner übergeben wird).</summary>
    public int MaxConnectionsPerWindow { get; init; } = 100;
    public TimeSpan ConnectionWindow { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>Ab dieser Dauer eines Verbindungsabbruchs werden verpasste 1-Min-Kerzen nachgeladen.</summary>
    public TimeSpan GapBackfillThreshold { get; init; } = TimeSpan.FromMinutes(2);
    public int HttpTimeoutSeconds { get; init; } = 20;
}

/// <summary>
/// Basis für WebSocket-Quellen: Verbindungsschleife mit exponentiellem Backoff + Jitter, Verbindungs-Ratenbegrenzung,
/// Ping und Timeout-Erkennung, erneutes Abonnieren nach Wiederverbindung, Lücken-Nachladen.
/// </summary>
public abstract class WebSocketMarketDataSource : MarketDataSourceBase
{
    private readonly IWebSocketConnectionFactory _factory;
    private readonly ExponentialBackoff _backoff;
    private readonly Lock _runLock = new();
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private volatile IWebSocketConnection? _current;

    protected WebSocketMarketDataSource(string name, Uri endpoint, WebSocketSourceOptions options, IWebSocketConnectionFactory factory,
        IClock clock, IRandomSource random, ConnectionRateLimiter? rateLimiter, ILogger? logger)
        : base(name, clock, logger)
    {
        Endpoint = endpoint;
        WsOptions = options;
        _factory = factory;
        _backoff = new ExponentialBackoff(options.Backoff, random);
        RateLimiter = rateLimiter ?? new ConnectionRateLimiter(options.MaxConnectionsPerWindow, options.ConnectionWindow, clock);
    }

    public Uri Endpoint { get; }
    protected WebSocketSourceOptions WsOptions { get; }
    public ConnectionRateLimiter RateLimiter { get; }
    /// <summary>Anzahl Verbindungsaufbauten (Diagnose/Tests).</summary>
    public int ConnectCount { get; private set; }
    /// <summary>Austauschbares Warten (Tests).</summary>
    public Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } = Task.Delay;

    public bool IsConnected => _current?.IsOpen == true && State == ConnectionState.Connected;

    // ---------- von Unterklassen zu liefern ----------
    protected abstract IEnumerable<string> BuildSubscribeMessages(IReadOnlyCollection<string> symbols);
    protected abstract IEnumerable<string> BuildUnsubscribeMessages(IReadOnlyCollection<string> symbols);
    /// <summary>Verarbeitet eine eingehende Nachricht (Ereignisse über <see cref="MarketDataSourceBase.Publish"/>).</summary>
    protected abstract Task HandleMessageAsync(string message, DateTimeOffset receivedUtc, CancellationToken ct);
    /// <summary>Anwendungs-Ping (null = keiner).</summary>
    protected virtual string? BuildPingMessage() => null;
    /// <summary>Nach (Wieder-)Verbindung: lokale Zustände (Orderbücher, Kerzen) zurücksetzen.</summary>
    protected virtual void OnConnectionReset() { }

    // ---------- Lebenszyklus ----------
    public override Task StartAsync(CancellationToken ct)
    {
        lock (_runLock)
        {
            if (_runTask is { IsCompleted: false }) return Task.CompletedTask;
            _runCts = new CancellationTokenSource();
            var token = _runCts.Token;
            _runTask = Task.Run(() => RunAsync(token), CancellationToken.None);
        }
        OnStarted();
        return Task.CompletedTask;
    }

    /// <summary>Hook nach dem Start (z. B. Symbolliste im Hintergrund laden).</summary>
    protected virtual void OnStarted() { }

    public override async Task StopAsync(CancellationToken ct)
    {
        await StopLoopAsync().ConfigureAwait(false);
        SetState(ConnectionState.Disconnected, "Gestoppt");
    }

    protected override Task DisconnectForSuspendAsync(CancellationToken ct) => StopLoopAsync();

    protected override Task ReconnectAfterSuspendAsync(CancellationToken ct) => StartAsync(ct);

    private async Task StopLoopAsync()
    {
        Task? task;
        CancellationTokenSource? cts;
        lock (_runLock)
        {
            task = _runTask;
            cts = _runCts;
            _runTask = null;
            _runCts = null;
        }
        if (cts is null) return;
        var conn = _current;
        if (conn is not null)
        {
            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await conn.CloseAsync(closeCts.Token).ConfigureAwait(false); } catch { /* egal beim Trennen */ }
        }
        cts.Cancel();
        if (task is not null)
        {
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Logger.LogDebug(ex, "{Source}: Schleife mit Fehler beendet", Name); }
        }
        cts.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        int failures = 0;
        bool everConnected = false;
        DateTimeOffset? lostAtUtc = null;
        string? lastError = null;

        while (!ct.IsCancellationRequested)
        {
            SetState(everConnected || failures > 0 ? ConnectionState.Reconnecting : ConnectionState.Connecting, lastError);

            // Ratenbegrenzung der Verbindungsversuche (Kraken sperrt sonst die IP).
            while (!RateLimiter.TryAcquire(out var wait))
            {
                SetState(ConnectionState.Reconnecting, $"Verbindungslimit erreicht ({RateLimiter.MaxConnections}/{RateLimiter.Window.TotalMinutes:0} min), warte {wait.TotalSeconds:0} s");
                try { await DelayAsync(wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(50), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }

            var conn = _factory.Create();
            bool connected = false;
            try
            {
                using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connectCts.CancelAfter(WsOptions.ConnectTimeout);
                    await conn.ConnectAsync(Endpoint, connectCts.Token).ConfigureAwait(false);
                }
                connected = true;
                ConnectCount++;
                failures = 0;
                lastError = null;
                everConnected = true;
                OnConnectionReset();
                _current = conn;
                SetState(ConnectionState.Connected);

                var symbols = Subscriptions;
                if (symbols.Count > 0)
                    foreach (var msg in BuildSubscribeMessages(symbols))
                        await conn.SendAsync(msg, ct).ConfigureAwait(false);

                if (lostAtUtc is { } lost)
                {
                    var now = Clock.UtcNow;
                    lostAtUtc = null;
                    if (now - lost >= WsOptions.GapBackfillThreshold)
                        _ = Task.Run(() => BackfillSafeAsync(lost, now, ct), CancellationToken.None);
                }

                await ReceiveLoopAsync(conn, ct).ConfigureAwait(false);
                lastError = "Verbindung von der Gegenseite geschlossen";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                lastError = ex is TimeoutException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
                Logger.LogWarning("{Source}: Verbindungsfehler: {Error}", Name, lastError);
            }
            finally
            {
                _current = null;
                try { await conn.DisposeAsync().ConfigureAwait(false); } catch { /* ignorieren */ }
            }

            if (ct.IsCancellationRequested) break;
            lostAtUtc ??= Clock.UtcNow;
            if (!connected) failures++;
            if (WsOptions.MaxConsecutiveFailures > 0 && failures >= WsOptions.MaxConsecutiveFailures)
            {
                SetState(ConnectionState.Failed, $"{failures} Verbindungsversuche fehlgeschlagen: {lastError}");
                return;
            }
            var delay = _backoff.GetDelay(Math.Max(0, failures - 1));
            SetState(ConnectionState.Reconnecting, $"{lastError}; neuer Versuch in {delay.TotalSeconds:0.#} s");
            try { await DelayAsync(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task BackfillSafeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        try { await BackfillGapAsync(from, to, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Logger.LogWarning(ex, "{Source}: Nachladen nach Verbindungsabbruch fehlgeschlagen", Name); }
    }

    private async Task ReceiveLoopAsync(IWebSocketConnection conn, CancellationToken ct)
    {
        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pingTask = BuildPingMessage() is null ? Task.CompletedTask : PingLoopAsync(conn, loopCts.Token);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? msg;
                // Ohne Ping und ohne Abos schickt manche Börse nichts – dann kein Timeout erzwingen.
                bool enforceTimeout = BuildPingMessage() is not null || Subscriptions.Count > 0;
                using (var rcvCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    if (enforceTimeout) rcvCts.CancelAfter(WsOptions.ReceiveTimeout);
                    try
                    {
                        msg = await conn.ReceiveAsync(rcvCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new TimeoutException($"Keine Daten seit {WsOptions.ReceiveTimeout.TotalSeconds:0} s – Verbindung gilt als tot");
                    }
                }
                if (msg is null) return;
                try
                {
                    await HandleMessageAsync(msg, Clock.UtcNow, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    // Eine kaputte Nachricht darf die Verbindung nicht beenden.
                    Logger.LogWarning(ex, "{Source}: Nachricht nicht verarbeitbar: {Message}", Name, Truncate(msg));
                }
            }
        }
        finally
        {
            loopCts.Cancel();
            try { await pingTask.ConfigureAwait(false); } catch { /* ignorieren */ }
        }
    }

    private async Task PingLoopAsync(IWebSocketConnection conn, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await DelayAsync(WsOptions.PingInterval, ct).ConfigureAwait(false);
            var ping = BuildPingMessage();
            if (ping is not null && conn.IsOpen) await conn.SendAsync(ping, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Sendet eine Nachricht, wenn verbunden (sonst still verworfen – Abos werden beim Verbinden ohnehin gesendet).</summary>
    protected async Task SendIfConnectedAsync(string message, CancellationToken ct)
    {
        var conn = _current;
        if (conn is null || !conn.IsOpen) return;
        try { await conn.SendAsync(message, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Logger.LogDebug(ex, "{Source}: Senden fehlgeschlagen", Name); }
    }

    protected override async Task OnSubscribedAsync(IReadOnlyList<string> symbols, CancellationToken ct)
    {
        foreach (var msg in BuildSubscribeMessages(symbols)) await SendIfConnectedAsync(msg, ct).ConfigureAwait(false);
    }

    protected override async Task OnUnsubscribedAsync(IReadOnlyList<string> symbols, CancellationToken ct)
    {
        foreach (var msg in BuildUnsubscribeMessages(symbols)) await SendIfConnectedAsync(msg, ct).ConfigureAwait(false);
    }

    protected static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";

    protected static int TimeframeMinutes(Timeframe tf) => (int)tf.ToTimeSpan().TotalMinutes;
}
