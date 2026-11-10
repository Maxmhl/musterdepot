using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using Musterdepot.App.ViewModels;
using Musterdepot.App.Views;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Commands;
using Musterdepot.Core.Domain;

namespace Musterdepot.App.Services;

/// <summary>
/// Freigabe-Warteschlange in der Oberfläche: hält die offenen Anfragen (geteilt zwischen KI-Aktivität und dem kleinen
/// Always-on-top-Fenster) und entscheidet über <see cref="ResolveApprovalCommand"/> (Source = Mensch).
/// </summary>
public sealed class ApprovalPopupService(
    AppServices services,
    IUiDispatcher ui,
    IDialogService dialogs,
    WpfUserNotifier notifier,
    ILogger<ApprovalPopupService> logger)
{
    private ApprovalWindow? _window;
    private bool _attached;

    /// <summary>Offene Anfragen (nur auf dem UI-Thread ändern).</summary>
    public ObservableCollection<ApprovalItemViewModel> Pending { get; } = [];

    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        if (services.ApprovalService is { } approvals)
        {
            approvals.RequestAdded += (_, r) => notifier.NotifyOnce(r);
            approvals.RequestResolved += (_, r) => ui.Post(() => Remove(r.ApprovalId));
            foreach (var r in approvals.Pending) Add(r, showWindow: false);
        }
        services.ReadModels?.PendingApprovals.Subscribe(ui, Sync);
    }

    /// <summary>Wird vom <see cref="WpfUserNotifier"/> aufgerufen (UI-Thread).</summary>
    public void Show(ApprovalRequest request) => Add(request, showWindow: true);

    private void Sync(IReadOnlyList<ApprovalRequest> pending)
    {
        var ids = pending.Where(p => p.State == ApprovalState.Pending).Select(p => p.ApprovalId).ToHashSet();
        foreach (var item in Pending.Where(i => !ids.Contains(i.ApprovalId)).ToList()) Pending.Remove(item);
        foreach (var r in pending.Where(p => p.State == ApprovalState.Pending)) Add(r, showWindow: false);
        UpdateWindow();
    }

    private void Add(ApprovalRequest request, bool showWindow)
    {
        if (request.State != ApprovalState.Pending) { Remove(request.ApprovalId); return; }
        if (Pending.All(p => p.ApprovalId != request.ApprovalId))
            Pending.Add(new ApprovalItemViewModel(request, DecideAsync));
        if (showWindow) OpenWindow();
        UpdateWindow();
    }

    private void Remove(string approvalId)
    {
        var item = Pending.FirstOrDefault(p => p.ApprovalId == approvalId);
        if (item is not null) Pending.Remove(item);
        UpdateWindow();
    }

    private void OpenWindow()
    {
        if (_window is null)
        {
            _window = new ApprovalWindow { DataContext = this };
            _window.Closed += (_, _) => _window = null;
        }
        if (!_window.IsVisible) _window.Show();
        _window.Topmost = true;
        _window.Activate();
    }

    private void UpdateWindow()
    {
        if (Pending.Count == 0 && _window is not null) _window.Close();
    }

    private async Task DecideAsync(ApprovalItemViewModel item, bool approved)
    {
        item.IsBusy = true;
        try
        {
            var result = await services.SendAsync(new ResolveApprovalCommand
            {
                ApprovalId = item.ApprovalId,
                Approved = approved,
                Source = CommandSource.Human,
                ClientId = "ui",
            });
            if (!result.Success && services.CommandBus is null && services.ApprovalService is { } direct)
            {
                // Ohne Befehlsschicht direkt an die Warteschlange (Notbetrieb).
                if (direct.Resolve(item.ApprovalId, approved, "ui")) result = CommandResult.Ok();
            }
            if (result.Success)
            {
                logger.LogInformation("Freigabe {Id} {Decision}", item.ApprovalId, approved ? "erlaubt" : "abgelehnt");
                Remove(item.ApprovalId);
            }
            else
            {
                dialogs.Error("Freigabe", "Entscheidung konnte nicht übermittelt werden: " + result.Message);
            }
        }
        finally
        {
            item.IsBusy = false;
        }
    }
}
