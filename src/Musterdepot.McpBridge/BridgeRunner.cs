using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Musterdepot.Core.Abstractions;

namespace Musterdepot.McpBridge;

/// <summary>
/// Brücke stdio ↔ Named Pipe für Claude Desktop.
/// Ablauf: mit der Pipe des laufenden Musterdepots verbinden, Handshake-Zeile mit Token senden, Antwort prüfen,
/// danach stdin → Pipe und Pipe → stdout byte-genau kopieren. Fehlermeldungen gehen ausschließlich auf stderr
/// (stdout gehört dem MCP-Protokoll).
/// </summary>
public static class BridgeRunner
{
    public const string TokenEnvironmentVariable = "MUSTERDEPOT_TOKEN";
    public const string PipeEnvironmentVariable = "MUSTERDEPOT_PIPE";

    public const int ExitOk = 0;
    public const int ExitUsage = 1;
    public const int ExitNotRunning = 2;
    public const int ExitHandshakeFailed = 3;

    public sealed record Options(string? Token, string PipeName, TimeSpan ConnectTimeout);

    public static Options ParseArgs(string[] args, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        string? token = null;
        string? pipe = null;
        var timeout = TimeSpan.FromSeconds(5);
        for (int i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Wert für {args[i]} fehlt.");
            switch (args[i])
            {
                case "--token" or "-t": token = Next(); break;
                case "--pipe": pipe = Next(); break;
                case "--timeout": timeout = TimeSpan.FromSeconds(double.Parse(Next()!, System.Globalization.CultureInfo.InvariantCulture)); break;
                case "--help" or "-h" or "/?": throw new ArgumentException("");
                default:
                    if (args[i].StartsWith("--token=", StringComparison.Ordinal)) token = args[i]["--token=".Length..];
                    else throw new ArgumentException($"Unbekanntes Argument '{args[i]}'.");
                    break;
            }
        }
        token ??= env(TokenEnvironmentVariable);
        pipe ??= env(PipeEnvironmentVariable);
        return new Options(string.IsNullOrWhiteSpace(token) ? null : token.Trim(), string.IsNullOrWhiteSpace(pipe) ? AppPaths.BridgePipeName : pipe.Trim(), timeout);
    }

    public const string Usage =
        "Musterdepot.McpBridge – verbindet Claude Desktop (stdio) mit dem laufenden Musterdepot (Named Pipe).\n" +
        "Aufruf: Musterdepot.McpBridge.exe [--token <token>] [--pipe <name>] [--timeout <sekunden>]\n" +
        "Das Token kann auch in der Umgebungsvariablen MUSTERDEPOT_TOKEN stehen (Token im Programm unter Einstellungen > KI-Clients erzeugen).";

    public static async Task<int> RunAsync(Options options, Stream stdin, Stream stdout, TextWriter stderr, CancellationToken ct = default)
    {
        await using var pipe = new NamedPipeClientStream(".", options.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(options.ConnectTimeout);
            await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException or UnauthorizedAccessException)
        {
            if (ct.IsCancellationRequested) return ExitOk;
            await stderr.WriteLineAsync(
                $"Musterdepot: Keine Verbindung zum Programm (Named Pipe '{options.PipeName}'). Läuft Musterdepot und ist der MCP-Server " +
                $"in den Einstellungen aktiviert? ({ex.GetType().Name}: {ex.Message})").ConfigureAwait(false);
            return ExitNotRunning;
        }

        // Handshake
        var hello = JsonSerializer.Serialize(new { type = "musterdepot-bridge", version = 1, token = options.Token });
        await pipe.WriteAsync(Encoding.UTF8.GetBytes(hello + "\n"), ct).ConfigureAwait(false);
        await pipe.FlushAsync(ct).ConfigureAwait(false);
        var replyLine = await ReadLineAsync(pipe, 8192, ct).ConfigureAwait(false);
        if (replyLine is null)
        {
            await stderr.WriteLineAsync("Musterdepot: Verbindung beim Handshake getrennt.").ConfigureAwait(false);
            return ExitHandshakeFailed;
        }
        try
        {
            using var doc = JsonDocument.Parse(replyLine);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            {
                var error = root.TryGetProperty("error", out var e) ? e.GetString() : "unbekannter Fehler";
                await stderr.WriteLineAsync($"Musterdepot: Anmeldung abgelehnt: {error}").ConfigureAwait(false);
                return ExitHandshakeFailed;
            }
            await stderr.WriteLineAsync($"Musterdepot: verbunden als {root.GetProperty("name").GetString()} ({root.GetProperty("client_id").GetString()}).").ConfigureAwait(false);
        }
        catch (JsonException)
        {
            await stderr.WriteLineAsync("Musterdepot: Unerwartete Antwort beim Handshake.").ConfigureAwait(false);
            return ExitHandshakeFailed;
        }

        // Byte-genaue Weiterleitung in beide Richtungen.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var up = PumpAsync(stdin, pipe, linked.Token);
        var down = PumpAsync(pipe, stdout, linked.Token);
        var first = await Task.WhenAny(up, down).ConfigureAwait(false);
        if (first == down && !ct.IsCancellationRequested)
            await stderr.WriteLineAsync("Musterdepot: Verbindung vom Programm beendet.").ConfigureAwait(false);
        await linked.CancelAsync().ConfigureAwait(false);
        // Konsolen-stdin lässt sich nicht überall abbrechen – nicht endlos auf den Lesevorgang warten.
        try { await Task.WhenAll(up, down).WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false); } catch { /* beendet */ }
        return ExitOk;
    }

    private static async Task PumpAsync(Stream from, Stream to, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            int n;
            while ((n = await from.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                await to.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Liest byteweise bis <c>\n</c>, damit nichts vom nachfolgenden MCP-Strom verschluckt wird.</summary>
    private static async Task<string?> ReadLineAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        var one = new byte[1];
        using var ms = new MemoryStream();
        while (true)
        {
            var n = await stream.ReadAsync(one, ct).ConfigureAwait(false);
            if (n == 0) return ms.Length == 0 ? null : Encoding.UTF8.GetString(ms.ToArray());
            if (one[0] == (byte)'\n') return Encoding.UTF8.GetString(ms.ToArray()).TrimEnd('\r');
            if (ms.Length >= maxBytes) throw new InvalidDataException("Antwortzeile zu lang.");
            ms.WriteByte(one[0]);
        }
    }
}
