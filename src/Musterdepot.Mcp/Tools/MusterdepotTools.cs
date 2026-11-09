using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Commands;
using Musterdepot.Core.Domain;

namespace Musterdepot.Mcp.Tools;

/// <summary>Ungültige Tool-Argumente (wird als <c>invalid_arguments</c> an die KI gemeldet).</summary>
public sealed class ToolArgumentException(string message) : Exception(message);

/// <summary>
/// Alle MCP-Tools des Musterdepots. Lesende Tools rufen <see cref="Core.ReadModels.ITradingQueries"/> und protokollieren
/// jeden Aufruf in <see cref="IAuditLog"/>. Schreibende Tools bauen ein <see cref="ICommand"/> (Source=Ai, ClientId aus der
/// Identität, IdempotencyKey) und rufen ausschließlich <see cref="ICommandBus.SendAsync"/> – die Befehlsschicht protokolliert selbst.
/// </summary>
public sealed partial class MusterdepotTools(McpToolBackend backend)
{
    public const string PendingApprovalHint =
        "Die Aktion wartet auf Freigabe durch den Menschen im Programm (KI-Aktivität). Du kannst sie nicht selbst freigeben; prüfe den Stand später mit list_orders oder get_agent_status.";

    /// <summary>Erzeugt die Tool-Liste (einmal pro Server). Jede Ausführung erhält eine neue Instanz aus den Request-Services.</summary>
    public static IReadOnlyList<McpServerTool> CreateTools()
    {
        var list = new List<McpServerTool>();
        foreach (var method in typeof(MusterdepotTools).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (method.GetCustomAttribute<McpServerToolAttribute>() is null) continue;
            list.Add(McpServerTool.Create(method, ctx => new MusterdepotTools(ResolveBackend(ctx)), new McpServerToolCreateOptions()));
        }
        return list;
    }

    /// <summary>Namen aller Tools (für Tests und Doku).</summary>
    public static IReadOnlyList<string> ToolNames { get; } =
        typeof(MusterdepotTools).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .OfType<string>().ToList();

    private static McpToolBackend ResolveBackend(RequestContext<CallToolRequestParams> ctx)
    {
        var sp = ctx.Services ?? ctx.Server.Services
                 ?? throw new InvalidOperationException("Keine Services im MCP-Kontext.");
        return (McpToolBackend?)sp.GetService(typeof(McpToolBackend))
               ?? throw new InvalidOperationException("McpToolBackend ist nicht registriert (AddMusterdepotMcp aufrufen).");
    }

    // ------------------------------------------------------------------ Lesen

    private async Task<CallToolResult> ReadAsync(
        RequestContext<CallToolRequestParams> ctx,
        Func<ClientIdentity, Task<ToolEnvelope>> body,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var identity = McpCaller.FromPrincipal(ctx.User);
        if (identity is null) return Unauthenticated();

        ToolEnvelope envelope;
        bool isError;
        if (!identity.Scopes.HasFlag(ClientScopes.Read))
        {
            envelope = new ToolEnvelope { Status = "denied", Message = "Dieser Client hat nicht das Recht 'read'." };
            isError = true;
        }
        else
        {
            try
            {
                envelope = await body(identity).ConfigureAwait(false);
                isError = envelope.Status is "error" or "invalid_arguments";
            }
            catch (ToolArgumentException ex)
            {
                envelope = new ToolEnvelope { Status = "invalid_arguments", Message = ex.Message };
                isError = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                backend.Logger.LogError(ex, "MCP-Tool {Tool} fehlgeschlagen", ctx.Params?.Name);
                envelope = new ToolEnvelope { Status = "error", Message = ex.Message };
                isError = true;
            }
        }

        var audit = await AuditAsync(ctx, identity, envelope.Status, envelope.Message, sw.Elapsed).ConfigureAwait(false);
        return (envelope with { AuditId = audit?.AuditId }).ToResult(isError);
    }

    private static Task<ToolEnvelope> Ok(object? data, string? message = null) =>
        Task.FromResult(new ToolEnvelope { Status = "ok", Data = data, Message = message });

    private static Task<ToolEnvelope> NotFound(string message) =>
        Task.FromResult(new ToolEnvelope { Status = "not_found", Message = message });

    private async Task<AuditEntry?> AuditAsync(RequestContext<CallToolRequestParams> ctx, ClientIdentity identity, string result, string? message, TimeSpan duration)
    {
        try
        {
            var entry = new AuditEntry
            {
                AuditId = $"aud_{Guid.CreateVersion7():N}",
                TimestampUtc = backend.Clock.UtcNow,
                Source = CommandSource.Ai,
                ClientId = identity.ClientId,
                Action = ctx.Params?.Name ?? "unknown",
                ParametersJson = ctx.Params?.Arguments is { } args ? JsonSerializer.Serialize(args, McpJson.Options) : null,
                Result = result,
                Message = message,
                Duration = duration,
            };
            // Protokoll-Schreiben soll nicht vom Abbruch der Anfrage abhängen.
            return await backend.AuditLog.AppendAsync(entry, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            backend.Logger.LogError(ex, "Protokolleintrag für {Tool} fehlgeschlagen", ctx.Params?.Name);
            return null;
        }
    }

    // ------------------------------------------------------------------ Schreiben

    private async Task<CallToolResult> WriteAsync(
        RequestContext<CallToolRequestParams> ctx,
        string? idempotencyKey,
        Func<ClientIdentity, CommandBase> build,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var identity = McpCaller.FromPrincipal(ctx.User);
        if (identity is null) return Unauthenticated();

        CommandBase command;
        try
        {
            var key = string.IsNullOrWhiteSpace(idempotencyKey) ? $"mcp_{Guid.CreateVersion7():N}" : idempotencyKey.Trim();
            if (key.Length > 200) throw new ToolArgumentException("idempotency_key darf höchstens 200 Zeichen lang sein.");
            command = build(identity) with { Source = CommandSource.Ai, ClientId = identity.ClientId, IdempotencyKey = key };
        }
        catch (ToolArgumentException ex)
        {
            return new ToolEnvelope { Status = "invalid_arguments", Message = ex.Message, IdempotencyKey = idempotencyKey }.ToResult(true);
        }

        // Rechte je Tool: der Befehl bestimmt den nötigen Bereich (z. B. place_order → trade, dry_run → read).
        if (command.HumanOnly || !identity.Scopes.HasFlag(command.RequiredScope))
        {
            var why = command.HumanOnly
                ? "Diese Aktion darf nur ein Mensch im Programm ausführen."
                : $"Dieser Client hat nicht das Recht '{ScopeName(command.RequiredScope)}'.";
            // Die Befehlsschicht sieht diesen Aufruf nie – darum hier protokollieren.
            var audit = await AuditAsync(ctx, identity, "denied", why, sw.Elapsed).ConfigureAwait(false);
            return new ToolEnvelope { Status = "denied", Message = why, AuditId = audit?.AuditId, IdempotencyKey = command.IdempotencyKey }.ToResult(true);
        }

        CommandResult result;
        try
        {
            result = await backend.CommandBus.SendAsync(command, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            backend.Logger.LogError(ex, "Befehl {Command} aus MCP-Tool {Tool} fehlgeschlagen", command.GetType().Name, ctx.Params?.Name);
            result = CommandResult.Fail(CommandStatus.Failed, ex.Message);
        }

        var pending = result.Status == CommandStatus.PendingApproval;
        var envelope = new ToolEnvelope
        {
            Status = McpJson.Status(result.Status),
            Message = pending ? JoinMessages(result.Message, PendingApprovalHint) : result.Message,
            Data = result.Data,
            AuditId = result.AuditId,
            ApprovalId = result.ApprovalId,
            RequiresHumanApproval = pending ? true : null,
            IdempotencyKey = command.IdempotencyKey,
        };
        return envelope.ToResult(result.Status is CommandStatus.Rejected or CommandStatus.Denied or CommandStatus.Failed);
    }

    private static CallToolResult Unauthenticated() =>
        new ToolEnvelope { Status = "denied", Message = "Nicht authentifiziert: gültiges Bearer-Token erforderlich." }.ToResult(true);

    private static string JoinMessages(string? a, string b) => string.IsNullOrWhiteSpace(a) ? b : $"{a} – {b}";

    internal static string ScopeName(ClientScopes scope) => scope switch
    {
        ClientScopes.Read => "read",
        ClientScopes.Trade => "trade",
        ClientScopes.Settings => "settings",
        _ => scope.ToString().ToLowerInvariant(),
    };

    internal static IReadOnlyList<string> ScopeNames(ClientScopes scopes) =>
        new[] { ClientScopes.Read, ClientScopes.Trade, ClientScopes.Settings }.Where(s => scopes.HasFlag(s)).Select(ScopeName).ToList();

    // ------------------------------------------------------------------ Parser

    internal static T ParseEnum<T>(string? value, string parameter, T? fallback = null) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback ?? throw new ToolArgumentException($"{parameter} fehlt. Erlaubt: {EnumValues<T>()}.");
        var normalized = value.Trim().Replace("_", "").Replace("-", "").Replace(" ", "");
        if (!normalized.All(char.IsLetterOrDigit) || char.IsDigit(normalized[0])
            || !Enum.TryParse<T>(normalized, ignoreCase: true, out var result) || !Enum.IsDefined(result))
            throw new ToolArgumentException($"Ungültiger Wert '{value}' für {parameter}. Erlaubt: {EnumValues<T>()}.");
        return result;
    }

    internal static string EnumValues<T>() where T : struct, Enum =>
        string.Join(", ", Enum.GetNames<T>().Select(n => JsonNamingPolicy.SnakeCaseLower.ConvertName(n)));

    internal static Timeframe ParseTimeframe(string? value)
    {
        var v = (value ?? "1m").Trim().ToLowerInvariant();
        return v switch
        {
            "1m" or "m1" or "1min" => Timeframe.M1,
            "5m" or "m5" or "5min" => Timeframe.M5,
            "15m" or "m15" or "15min" => Timeframe.M15,
            "30m" or "m30" or "30min" => Timeframe.M30,
            "1h" or "h1" or "60m" => Timeframe.H1,
            "4h" or "h4" => Timeframe.H4,
            "1d" or "d1" or "day" => Timeframe.D1,
            "1w" or "w1" or "week" => Timeframe.W1,
            _ => throw new ToolArgumentException($"Ungültiger timeframe '{value}'. Erlaubt: 1m, 5m, 15m, 30m, 1h, 4h, 1d, 1w."),
        };
    }

    internal static DateTimeOffset? ParseTime(string? value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t))
            return t;
        throw new ToolArgumentException($"{parameter} ist kein gültiger Zeitpunkt (ISO 8601, z. B. 2026-11-09T14:30:00Z).");
    }

    internal static string RequireText(string? value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ToolArgumentException($"{parameter} fehlt.");
        return value.Trim();
    }

    internal static decimal? Positive(decimal? value, string parameter)
    {
        if (value is { } v && v <= 0) throw new ToolArgumentException($"{parameter} muss größer als 0 sein.");
        return value;
    }

    internal static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max);
}
