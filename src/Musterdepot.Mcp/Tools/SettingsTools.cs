using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Musterdepot.Core.Commands;
using Musterdepot.Core.Domain;

namespace Musterdepot.Mcp.Tools;

public sealed partial class MusterdepotTools
{
    [McpServerTool(Name = "get_settings", Title = "Einstellungen abfragen", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Programmeinstellungen: Datenquellen, Gebührenmodelle, Risiko-Limits, Ausführungsannahmen, MCP-Port und KI-Clients " +
                 "(ohne Tokens/Token-Hashes). Limits und Modi kann nur der Mensch im Programm ändern.")]
    public Task<CallToolResult> GetSettings(
        RequestContext<CallToolRequestParams> context,
        CancellationToken cancellationToken = default) =>
        ReadAsync(context, async _ =>
        {
            var settings = await backend.Queries.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
            return new ToolEnvelope { Status = "ok", Data = SettingsView.From(settings) };
        }, cancellationToken);

    [McpServerTool(Name = "update_settings", Title = "Einstellungen ändern", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Ändert unkritische Einstellungen (Theme, Autostart, Infobereich, Datenquellen an/aus). Risiko-Limits, Gebühren, " +
                 "KI-Modi, Rechte, Startkapital und MCP-Port sind für die KI gesperrt. Nur angegebene Felder werden geändert. Benötigt das Recht 'settings'.")]
    public Task<CallToolResult> UpdateSettings(
        RequestContext<CallToolRequestParams> context,
        [Description("Theme: system, light oder dark.")] string? theme = null,
        [Description("Programm mit Windows starten.")] bool? autostart = null,
        [Description("Beim Schließen in den Infobereich minimieren.")] bool? minimize_to_tray = null,
        [Description("Datenquelle Kraken an/aus.")] bool? kraken_enabled = null,
        [Description("Datenquelle Bitvavo an/aus.")] bool? bitvavo_enabled = null,
        [Description("Datenquelle Coinbase an/aus.")] bool? coinbase_enabled = null,
        [Description("Datenquelle gettex an/aus.")] bool? gettex_enabled = null,
        [Description("Datenquelle Cboe Europe an/aus.")] bool? cboe_enabled = null,
        [Description("EZB-Referenzkurse an/aus.")] bool? ecb_enabled = null,
        [Description("Optional: eindeutiger Schlüssel.")] string? idempotency_key = null,
        CancellationToken cancellationToken = default)
    {
        // Aktuellen Stand lesen (ohne Nebenwirkung), dann nur erlaubte Felder ändern.
        return UpdateSettingsCoreAsync();

        async Task<CallToolResult> UpdateSettingsCoreAsync()
        {
            AppSettings current;
            try { current = await backend.Queries.GetSettingsAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new ToolEnvelope { Status = "error", Message = $"Einstellungen konnten nicht gelesen werden: {ex.Message}" }.ToResult(true);
            }

            return await WriteAsync(context, idempotency_key, _ =>
            {
                if (theme is null && autostart is null && minimize_to_tray is null && kraken_enabled is null && bitvavo_enabled is null
                    && coinbase_enabled is null && gettex_enabled is null && cboe_enabled is null && ecb_enabled is null)
                    throw new ToolArgumentException("Keine Änderung angegeben.");
                var t = theme?.Trim().ToLowerInvariant();
                if (t is not null and not ("system" or "light" or "dark"))
                    throw new ToolArgumentException("theme muss system, light oder dark sein.");

                var ds = current.DataSources;
                var updated = current with
                {
                    Theme = t ?? current.Theme,
                    Autostart = autostart ?? current.Autostart,
                    MinimizeToTray = minimize_to_tray ?? current.MinimizeToTray,
                    DataSources = ds with
                    {
                        KrakenEnabled = kraken_enabled ?? ds.KrakenEnabled,
                        BitvavoEnabled = bitvavo_enabled ?? ds.BitvavoEnabled,
                        CoinbaseEnabled = coinbase_enabled ?? ds.CoinbaseEnabled,
                        GettexEnabled = gettex_enabled ?? ds.GettexEnabled,
                        CboeEnabled = cboe_enabled ?? ds.CboeEnabled,
                        EcbEnabled = ecb_enabled ?? ds.EcbEnabled,
                    },
                    // Limits, Modi/Clients, Gebühren usw. bleiben unverändert.
                };
                return new UpdateSettingsCommand { Settings = updated };
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    [McpServerTool(Name = "get_audit_log", Title = "Protokoll abfragen", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Protokoll der Aktionen (Mensch und KI): Zeit, Client, Aktion, Parameter, Ergebnis, Freigebender. Neueste zuerst.")]
    public Task<CallToolResult> GetAuditLog(
        RequestContext<CallToolRequestParams> context,
        [Description("Optional: ab Zeitpunkt (ISO 8601 UTC).")] string? from_utc = null,
        [Description("Optional: nur dieser Client (z. B. 'ui' für die Oberfläche).")] string? client_id = null,
        [Description("true = nur Einträge des eigenen Clients.")] bool only_mine = false,
        [Description("Maximale Anzahl (1–500).")] int limit = 50,
        CancellationToken cancellationToken = default) =>
        ReadAsync(context, async identity =>
        {
            var filter = only_mine ? identity.ClientId : string.IsNullOrWhiteSpace(client_id) ? null : client_id.Trim();
            var entries = await backend.Queries.GetAuditLogAsync(ParseTime(from_utc, "from_utc"), filter, Clamp(limit, 1, 500), cancellationToken).ConfigureAwait(false);
            var list = entries.OrderByDescending(e => e.TimestampUtc).ToList();
            return new ToolEnvelope { Status = "ok", Data = new { count = list.Count, entries = list } };
        }, cancellationToken);

    [McpServerTool(Name = "get_agent_status", Title = "KI-Status abfragen", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Status dieses KI-Clients: Modus (read_only, confirm, autonomous), Rechte, Limits, Orders in der letzten Minute, " +
                 "offene Freigaben, Not-Aus und Tagesverlustgrenze. Vor dem Handeln aufrufen.")]
    public Task<CallToolResult> GetAgentStatus(
        RequestContext<CallToolRequestParams> context,
        CancellationToken cancellationToken = default) =>
        ReadAsync(context, identity =>
        {
            var status = backend.Queries.GetAgentStatus(identity.ClientId);
            return Ok(new
            {
                status.ClientId,
                status.ClientName,
                status.Mode,
                scopes = ScopeNames(status.Scopes),
                status.KillSwitchEngaged,
                status.DailyLossLimitHit,
                status.OrdersLastMinute,
                status.MaxOrdersPerMinute,
                status.PendingApprovals,
                status.Limits,
                status.LastActivityUtc,
            });
        }, cancellationToken);

    [McpServerTool(Name = "engage_kill_switch", Title = "Not-Aus auslösen", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Löst den Not-Aus aus: Handel wird gestoppt. Die KI darf ihn auslösen, aber nur ein Mensch kann ihn per Klick im Programm wieder lösen. " +
                 "Verwenden, wenn etwas offensichtlich schiefläuft (z. B. unerwartete Orders, Datenfehler).")]
    public Task<CallToolResult> EngageKillSwitch(
        RequestContext<CallToolRequestParams> context,
        [Description("Begründung, die der Mensch im Programm sieht.")] string reason,
        [Description("Optional: eindeutiger Schlüssel.")] string? idempotency_key = null,
        CancellationToken cancellationToken = default) =>
        WriteAsync(context, idempotency_key, _ => new EngageKillSwitchCommand { Reason = RequireText(reason, "reason") }, cancellationToken);
}

/// <summary>Einstellungen ohne Geheimnisse: KI-Clients ohne Token-Hash.</summary>
public sealed record SettingsView
{
    public required int McpPort { get; init; }
    public required bool McpEnabled { get; init; }
    public required bool Autostart { get; init; }
    public required bool MinimizeToTray { get; init; }
    public required string DefaultAccountId { get; init; }
    public required decimal InitialCash { get; init; }
    public required string BaseCurrency { get; init; }
    public required string Theme { get; init; }
    public required DataSourceSettings DataSources { get; init; }
    public required FeeModelConfig CryptoFees { get; init; }
    public required FeeModelConfig StockFees { get; init; }
    public required RiskConfig Risk { get; init; }
    public required ExecutionConfig Execution { get; init; }
    public required IReadOnlyList<AiClientView> AiClients { get; init; }

    public static SettingsView From(AppSettings s) => new()
    {
        McpPort = s.McpPort,
        McpEnabled = s.McpEnabled,
        Autostart = s.Autostart,
        MinimizeToTray = s.MinimizeToTray,
        DefaultAccountId = s.DefaultAccountId,
        InitialCash = s.InitialCash,
        BaseCurrency = s.BaseCurrency,
        Theme = s.Theme,
        DataSources = s.DataSources,
        CryptoFees = s.CryptoFees,
        StockFees = s.StockFees,
        Risk = s.Risk,
        Execution = s.Execution,
        AiClients = s.AiClients.Select(c => new AiClientView(c.ClientId, c.Name, c.Mode, MusterdepotTools.ScopeNames(c.Scopes), c.Enabled, c.CreatedUtc)).ToList(),
    };
}

public sealed record AiClientView(string ClientId, string Name, ClientMode Mode, IReadOnlyList<string> Scopes, bool Enabled, DateTimeOffset CreatedUtc);
