using Microsoft.Extensions.DependencyInjection;
using Musterdepot.Core.Abstractions;

namespace Musterdepot.App.Services;

/// <summary>
/// <see cref="IUserNotifier"/> der Oberfläche: Windows-Benachrichtigung über das Tray-Symbol und bei Freigabeanfragen
/// zusätzlich ein kleines Always-on-top-Fenster mit Erlauben/Ablehnen. Aufrufe kommen von Hintergrund-Threads.
/// </summary>
/// <remarks>
/// Tray und Freigabe-Fenster werden erst beim Aufruf aufgelöst: Die Befehlsschicht hängt selbst von
/// <see cref="IUserNotifier"/> ab, eine direkte Abhängigkeit wäre ein Zyklus.
/// </remarks>
public sealed class WpfUserNotifier(IUiDispatcher ui, IServiceProvider sp) : IUserNotifier
{
    private readonly HashSet<string> _shown = [];

    public void Notify(string title, string message) =>
        ui.Post(() => sp.GetRequiredService<TrayService>().ShowNotification(title, message));

    public void NotifyApprovalRequested(ApprovalRequest request) => ui.Post(() =>
    {
        // Nur einmal pro Anfrage benachrichtigen (die Warteschlange meldet sich zusätzlich per Ereignis).
        if (!_shown.Add(request.ApprovalId)) return;
        sp.GetRequiredService<TrayService>().ShowNotification($"Freigabe nötig: {request.ClientName}", request.Summary, warning: true);
        sp.GetRequiredService<ApprovalPopupService>().Show(request);
    });

    /// <summary>Balloon für Anfragen, die nur über das Ereignis der Warteschlange ankamen.</summary>
    internal void NotifyOnce(ApprovalRequest request) => NotifyApprovalRequested(request);
}
