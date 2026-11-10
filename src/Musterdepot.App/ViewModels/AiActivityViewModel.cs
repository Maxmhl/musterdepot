using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Musterdepot.App.Services;
using Musterdepot.Charting;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Commands;
using Musterdepot.Core.Domain;

namespace Musterdepot.App.ViewModels;

public sealed record AgentStatusItem(
    string ClientId, string Name, string Mode, string Scopes, string Orders, string Approvals, string LastActivity,
    bool Blocked, string? BlockReason);

/// <summary>KI-Aktivität: Protokoll aller Tool-Aufrufe, Freigabe-Warteschlange, Status je KI-Client, großer Not-Aus.</summary>
public sealed partial class AiActivityViewModel(
    AppServices services,
    IUiDispatcher ui,
    IDialogService dialogs,
    ApprovalPopupService approvals,
    ILogger<AiActivityViewModel> logger) : PageViewModel
{
    private readonly List<IDisposable> _subscriptions = [];
    private IReadOnlyList<AuditEntry> _audit = [];
    private DispatcherTimer? _statusTimer;

    public override AppPage Page => AppPage.AiActivity;
    public override string Title => "KI-Aktivität";
    public override string Glyph => "";

    public ObservableCollection<AuditRowViewModel> AuditEntries { get; } = [];
    public ObservableCollection<ApprovalItemViewModel> PendingApprovals => approvals.Pending;
    public ObservableCollection<AgentStatusItem> Agents { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EngageKillSwitchCommand), nameof(ReleaseKillSwitchCommand))]
    private bool _killSwitchEngaged;

    [ObservableProperty] private bool _onlyAi = true;
    [ObservableProperty] private string _clientFilter = "";
    [ObservableProperty] private AuditRowViewModel? _selectedAudit;

    public override void Initialize()
    {
        if (services.ReadModels is { } rm)
        {
            _subscriptions.Add(rm.KillSwitch.Subscribe(ui, v => KillSwitchEngaged = v));
            _subscriptions.Add(rm.AuditLog.Subscribe(ui, a => { _audit = a ?? []; ApplyAudit(); }, TimeSpan.FromMilliseconds(400)));
        }
        else
        {
            SetStatus("Protokoll nicht verfügbar: Lesemodelle fehlen.", true);
        }
        if (services.ClientRegistry is { } reg) reg.Changed += (_, _) => ui.Post(RefreshAgents);

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _statusTimer.Tick += (_, _) => { if (IsActive) RefreshAgents(); };
        _statusTimer.Start();
    }

    protected override void OnActivated() => RefreshAgents();

    partial void OnOnlyAiChanged(bool value) => ApplyAudit();
    partial void OnClientFilterChanged(string value) => ApplyAudit();

    private void ApplyAudit()
    {
        AuditEntries.Clear();
        var filter = ClientFilter.Trim();
        foreach (var e in _audit
                     .Where(e => !OnlyAi || e.Source == CommandSource.Ai)
                     .Where(e => filter.Length == 0 || e.ClientId.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                                 e.Action.Contains(filter, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(e => e.TimestampUtc)
                     .Take(500))
            AuditEntries.Add(new AuditRowViewModel(e));
    }

    private IReadOnlyList<AiClientConfig> Clients =>
        services.ClientRegistry?.Clients ?? services.ReadModels?.Settings.Current?.AiClients ?? [];

    [RelayCommand]
    private void RefreshAgents()
    {
        Agents.Clear();
        foreach (var c in Clients)
        {
            try
            {
                var s = services.Queries?.GetAgentStatus(c.ClientId);
                var blocked = !c.Enabled || s is { KillSwitchEngaged: true } || s is { DailyLossLimitHit: true };
                var reason = !c.Enabled ? "deaktiviert" : s?.KillSwitchEngaged == true ? "Not-Aus aktiv" : s?.DailyLossLimitHit == true ? "Tagesverlust erreicht" : null;
                Agents.Add(new AgentStatusItem(
                    c.ClientId,
                    c.Name,
                    Labels.Mode(s?.Mode ?? c.Mode),
                    ScopesText(s?.Scopes ?? c.Scopes),
                    s is null ? "–" : $"{s.OrdersLastMinute}/{s.MaxOrdersPerMinute} pro Min",
                    s is null ? "–" : s.PendingApprovals.ToString(),
                    s?.LastActivityUtc is { } t ? $"{t.ToLocalTime():dd.MM. HH:mm:ss} ({DisplayFormat.Age(t, DateTimeOffset.UtcNow)})" : "noch nie",
                    blocked,
                    reason));
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Status für {Client} nicht verfügbar", c.ClientId);
            }
        }
    }

    internal static string ScopesText(ClientScopes s)
    {
        var parts = new List<string>();
        if (s.HasFlag(ClientScopes.Read)) parts.Add("lesen");
        if (s.HasFlag(ClientScopes.Trade)) parts.Add("handeln");
        if (s.HasFlag(ClientScopes.Settings)) parts.Add("Einstellungen");
        return parts.Count == 0 ? "keine" : string.Join(", ", parts);
    }

    private bool CanEngage() => !KillSwitchEngaged;
    private bool CanRelease() => KillSwitchEngaged;

    [RelayCommand(CanExecute = nameof(CanEngage))]
    private async Task EngageKillSwitch()
    {
        var r = await services.SendAsync(new EngageKillSwitchCommand
        {
            Reason = "Not-Aus in der Oberfläche ausgelöst",
            Source = CommandSource.Human,
            ClientId = "ui",
        });
        SetStatus(r.Success ? "Not-Aus ausgelöst: offene Orders werden storniert, neue Orders sind gesperrt." : "Not-Aus fehlgeschlagen: " + r.Message, !r.Success);
        if (!r.Success) dialogs.Error("Not-Aus", "Not-Aus konnte nicht ausgelöst werden:\n" + r.Message);
    }

    /// <summary>Lösen nur hier per Klick durch einen Menschen.</summary>
    [RelayCommand(CanExecute = nameof(CanRelease))]
    private async Task ReleaseKillSwitch()
    {
        if (!dialogs.Confirm("Not-Aus lösen",
                "Not-Aus wirklich lösen?\n\nDanach dürfen wieder Orders erteilt werden – auch von KI-Clients im Modus „autonomous“.",
                warning: true))
            return;
        var r = await services.SendAsync(new ReleaseKillSwitchCommand { Source = CommandSource.Human, ClientId = "ui" });
        SetStatus(r.Success ? "Not-Aus gelöst." : "Lösen fehlgeschlagen: " + r.Message, !r.Success);
    }

    [RelayCommand]
    private void ExportAudit(string? format)
    {
        try
        {
            var german = string.Equals(format, "de", StringComparison.OrdinalIgnoreCase);
            var f = german ? CsvFormat.GermanExcel() : CsvFormat.Invariant;
            var path = dialogs.SaveText($"Musterdepot-KI-Protokoll-{DateTime.Now:yyyyMMdd-HHmm}.csv",
                "CSV-Datei (*.csv)|*.csv", TradeCsv.Audit(_audit, f), f.Encoding);
            if (path is not null) SetStatus($"Exportiert: {path}");
        }
        catch (Exception ex)
        {
            SetStatus("Export fehlgeschlagen: " + ex.GetBaseException().Message, true);
        }
    }
}
