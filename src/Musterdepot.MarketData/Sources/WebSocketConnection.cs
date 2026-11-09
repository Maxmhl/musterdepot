using System.Buffers;
using System.Net.WebSockets;
using System.Text;

namespace Musterdepot.MarketData.Sources;

/// <summary>Textbasierte WebSocket-Verbindung (austauschbar für Tests).</summary>
public interface IWebSocketConnection : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, CancellationToken ct);
    Task SendAsync(string message, CancellationToken ct);
    /// <summary>Nächste vollständige Textnachricht; null, wenn die Gegenseite geschlossen hat.</summary>
    Task<string?> ReceiveAsync(CancellationToken ct);
    Task CloseAsync(CancellationToken ct);
    bool IsOpen { get; }
}

public interface IWebSocketConnectionFactory
{
    IWebSocketConnection Create();
}

public sealed class ClientWebSocketConnectionFactory : IWebSocketConnectionFactory
{
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(20);
    public IWebSocketConnection Create() => new ClientWebSocketConnection(KeepAliveInterval);
}

/// <summary>Implementierung über <see cref="ClientWebSocket"/>; setzt fragmentierte Nachrichten zusammen, serialisiert Sendevorgänge.</summary>
public sealed class ClientWebSocketConnection : IWebSocketConnection
{
    private readonly ClientWebSocket _ws = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly byte[] _buffer = new byte[64 * 1024];

    public ClientWebSocketConnection(TimeSpan keepAlive)
    {
        _ws.Options.KeepAliveInterval = keepAlive;
        _ws.Options.SetRequestHeader("User-Agent", "Musterdepot/0.1");
    }

    public bool IsOpen => _ws.State == WebSocketState.Open;

    public Task ConnectAsync(Uri uri, CancellationToken ct) => _ws.ConnectAsync(uri, ct);

    public async Task SendAsync(string message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try { await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
        finally { _sendGate.Release(); }
    }

    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        var writer = new ArrayBufferWriter<byte>();
        while (true)
        {
            var r = await _ws.ReceiveAsync(_buffer.AsMemory(), ct).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            writer.Write(_buffer.AsSpan(0, r.Count));
            if (r.EndOfMessage) break;
        }
        return Encoding.UTF8.GetString(writer.WrittenSpan);
    }

    public async Task CloseAsync(CancellationToken ct)
    {
        try
        {
            if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", ct).ConfigureAwait(false);
        }
        catch (Exception) when (ct.IsCancellationRequested || _ws.State != WebSocketState.Open) { }
        catch (WebSocketException) { }
    }

    public ValueTask DisposeAsync()
    {
        _ws.Dispose();
        _sendGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
