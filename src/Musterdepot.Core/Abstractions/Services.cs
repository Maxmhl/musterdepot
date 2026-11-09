using Musterdepot.Core.Commands;
using Musterdepot.Core.Domain;
using Musterdepot.Core.Events;

namespace Musterdepot.Core.Abstractions;

public sealed record Watchlist
{
    public required string WatchlistId { get; init; }
    public required string Name { get; init; }
    public IReadOnlyList<string> Symbols { get; init; } = [];
    public DateTimeOffset CreatedUtc { get; init; }
}

public sealed record PriceAlert
{
    public required string AlertId { get; init; }
    public required string Symbol { get; init; }
    public required AlertCondition Condition { get; init; }
    public required decimal Threshold { get; init; }
    public string? Note { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset? TriggeredUtc { get; init; }
    public bool IsActive => TriggeredUtc is null;
}

/// <summary>Protokolleintrag jeder Aktion: Zeit, Client, Parameter, Ergebnis, wer freigegeben hat.</summary>
public sealed record AuditEntry
{
    public required string AuditId { get; init; }
    public required DateTimeOffset TimestampUtc { get; init; }
    public required CommandSource Source { get; init; }
    public required string ClientId { get; init; }
    /// <summary>Befehls- oder Tool-Name, z. B. "PlaceOrderCommand" oder "get_quote".</summary>
    public required string Action { get; init; }
    /// <summary>Parameter als JSON.</summary>
    public string? ParametersJson { get; init; }
    public required string Result { get; init; }
    public string? Message { get; init; }
    public string? ApprovedBy { get; init; }
    public string? IdempotencyKey { get; init; }
    public TimeSpan? Duration { get; init; }
}

// ---- Persistence (SQLite unter %LOCALAPPDATA%) ----
public interface IEventStore
{
    Task AppendAsync(IReadOnlyList<DomainEvent> events, CancellationToken ct = default);
    Task<IReadOnlyList<DomainEvent>> ReadAllAsync(string? accountId = null, CancellationToken ct = default);
}

public interface ICandleStore
{
    Task UpsertAsync(IReadOnlyList<Candle> candles, CancellationToken ct = default);
    Task<IReadOnlyList<Candle>> GetAsync(string symbol, Timeframe timeframe, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, CancellationToken ct = default);
    /// <summary>1-Minuten-Kerzen älter als <paramref name="minuteRetention"/> löschen (Standard 90 Tage).</summary>
    Task PruneAsync(TimeSpan minuteRetention, CancellationToken ct = default);
}

public interface IAuditLog
{
    Task<AuditEntry> AppendAsync(AuditEntry entry, CancellationToken ct = default);
    Task<IReadOnlyList<AuditEntry>> QueryAsync(DateTimeOffset? fromUtc = null, string? clientId = null, int limit = 200, CancellationToken ct = default);
    event EventHandler<AuditEntry>? EntryAppended;
}

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(AppSettings settings, CancellationToken ct = default);
}

public interface IWatchlistStore
{
    Task<IReadOnlyList<Watchlist>> GetAllAsync(CancellationToken ct = default);
    Task SaveAsync(Watchlist watchlist, CancellationToken ct = default);
    Task DeleteAsync(string watchlistId, CancellationToken ct = default);
}

public interface IAlertStore
{
    Task<IReadOnlyList<PriceAlert>> GetAllAsync(CancellationToken ct = default);
    Task SaveAsync(PriceAlert alert, CancellationToken ct = default);
    Task DeleteAsync(string alertId, CancellationToken ct = default);
}

public interface IInstrumentStore
{
    Task<IReadOnlyList<Instrument>> GetAllAsync(CancellationToken ct = default);
    Task UpsertAsync(IReadOnlyList<Instrument> instruments, CancellationToken ct = default);
}

/// <summary>Pfade: SQLite-Datei unter %LOCALAPPDATA%\Musterdepot.</summary>
public static class AppPaths
{
    public static string DataDirectory
    {
        get
        {
            var overrideDir = Environment.GetEnvironmentVariable("MUSTERDEPOT_DATA_DIR");
            var dir = !string.IsNullOrWhiteSpace(overrideDir)
                ? overrideDir
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Musterdepot");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
    public static string DatabasePath => Path.Combine(DataDirectory, "musterdepot.db");
    public static string RecordingsDirectory => Path.Combine(DataDirectory, "recordings");
    public static string TokensDirectory => Path.Combine(DataDirectory, "tokens");
    public static string LogsDirectory => Path.Combine(DataDirectory, "logs");
    /// <summary>Named Pipe der Brücke für Claude Desktop.</summary>
    public const string BridgePipeName = "Musterdepot.Mcp";
    public const int DefaultMcpPort = 47123;
}

// ---- Freigabe-Warteschlange ----
public enum ApprovalState { Pending, Approved, Denied, Expired }

public sealed record ApprovalRequest
{
    public required string ApprovalId { get; init; }
    public required DateTimeOffset CreatedUtc { get; init; }
    public required string ClientId { get; init; }
    public required string ClientName { get; init; }
    public required ICommand Command { get; init; }
    /// <summary>Lesbare Zusammenfassung, z. B. "Kauf 0,1 BTC/EUR Limit 60.000 €".</summary>
    public required string Summary { get; init; }
    public ApprovalState State { get; init; } = ApprovalState.Pending;
    public string? DecidedBy { get; init; }
    public DateTimeOffset? DecidedUtc { get; init; }
}

/// <summary>Freigabe-Warteschlange (Modus confirm). Oberfläche zeigt Benachrichtigung mit Erlauben/Ablehnen.</summary>
public interface IApprovalService
{
    event EventHandler<ApprovalRequest>? RequestAdded;
    event EventHandler<ApprovalRequest>? RequestResolved;
    IReadOnlyList<ApprovalRequest> Pending { get; }
    /// <summary>Wartet auf Entscheidung oder Timeout (→ Expired).</summary>
    Task<ApprovalRequest> RequestAsync(ApprovalRequest request, TimeSpan timeout, CancellationToken ct = default);
    bool Resolve(string approvalId, bool approved, string decidedBy);
}

/// <summary>Benachrichtigung in der Oberfläche (Windows-Toast/Tray). Optional, Standard: nichts.</summary>
public interface IUserNotifier
{
    void Notify(string title, string message);
    void NotifyApprovalRequested(ApprovalRequest request);
}

// ---- KI-Clients ----
public sealed record AgentStatus
{
    public required string ClientId { get; init; }
    public required string ClientName { get; init; }
    public required ClientMode Mode { get; init; }
    public required ClientScopes Scopes { get; init; }
    public bool KillSwitchEngaged { get; init; }
    public bool DailyLossLimitHit { get; init; }
    public int OrdersLastMinute { get; init; }
    public int MaxOrdersPerMinute { get; init; }
    public int PendingApprovals { get; init; }
    public RiskConfig Limits { get; init; } = RiskConfig.Default;
    public DateTimeOffset? LastActivityUtc { get; init; }
}

public sealed record ClientIdentity(string ClientId, string Name, ClientMode Mode, ClientScopes Scopes)
{
    public static ClientIdentity Ui { get; } = new("ui", "Oberfläche", ClientMode.Autonomous, ClientScopes.All);
}

/// <summary>Token pro Client, per Windows-DPAPI verschlüsselt im Benutzerprofil.</summary>
public interface ITokenService
{
    /// <summary>Erzeugt ein neues zufälliges Token und gibt es einmalig im Klartext zurück.</summary>
    Task<(AiClientConfig Client, string Token)> CreateClientAsync(string name, ClientScopes scopes, ClientMode mode, CancellationToken ct = default);
    Task<ClientIdentity?> ValidateAsync(string token, CancellationToken ct = default);
    Task RevokeAsync(string clientId, CancellationToken ct = default);
    Task<string?> GetTokenAsync(string clientId, CancellationToken ct = default);
}

/// <summary>Verwaltet KI-Client-Konfigurationen (Modus nur in der Oberfläche änderbar).</summary>
public interface IClientRegistry
{
    IReadOnlyList<AiClientConfig> Clients { get; }
    AiClientConfig? Get(string clientId);
    event EventHandler? Changed;
}

/// <summary>MCP-Server im selben Prozess (Kestrel auf 127.0.0.1).</summary>
public interface IMcpServerHost
{
    bool IsRunning { get; }
    Uri? Endpoint { get; }
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}
