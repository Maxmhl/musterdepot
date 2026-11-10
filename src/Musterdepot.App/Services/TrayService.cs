using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Extensions.Logging;
using Musterdepot.Core.Commands;
using Musterdepot.Core.Domain;

namespace Musterdepot.App.Services;

/// <summary>
/// Symbol im Infobereich: Öffnen, Not-Aus, Beenden. Schließen des Hauptfensters minimiert hierhin, die
/// Datenverbindungen laufen weiter.
/// </summary>
public sealed class TrayService(AppServices services, IDialogService dialogs, ILogger<TrayService> logger) : IDisposable
{
    private TaskbarIcon? _icon;
    private MenuItem? _killItem;

    public event EventHandler? OpenRequested;
    public event EventHandler? ExitRequested;

    public bool IsCreated => _icon is not null;

    public void Create()
    {
        if (_icon is not null) return;
        try
        {
            var menu = new ContextMenu();
            var open = new MenuItem { Header = "Musterdepot öffnen", FontWeight = FontWeights.SemiBold };
            open.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
            _killItem = new MenuItem
            {
                Header = "NOT-AUS auslösen",
                Foreground = new SolidColorBrush(Color.FromRgb(0xD3, 0x2F, 0x2F)),
                FontWeight = FontWeights.Bold,
                ToolTip = "Storniert alle offenen Orders und sperrt neue Orders. Lösen nur im Programm (KI-Aktivität).",
            };
            _killItem.Click += async (_, _) => await EngageKillSwitchAsync();
            var exit = new MenuItem { Header = "Beenden" };
            exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
            menu.Items.Add(open);
            menu.Items.Add(new Separator());
            menu.Items.Add(_killItem);
            menu.Items.Add(new Separator());
            menu.Items.Add(exit);

            _icon = new TaskbarIcon
            {
                ToolTipText = "Musterdepot – Paper Trading (kein echtes Geld)",
                IconSource = new BitmapImage(new Uri("pack://application:,,,/Musterdepot;component/Assets/app.ico", UriKind.Absolute)),
                ContextMenu = menu,
                NoLeftClickDelay = true,
            };
            _icon.TrayMouseDoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
            _icon.TrayLeftMouseUp += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
            _icon.ForceCreate(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Symbol im Infobereich konnte nicht erstellt werden");
            _icon = null;
        }
    }

    /// <summary>Zeigt den Not-Aus-Zustand im Menü.</summary>
    public void SetKillSwitchState(bool engaged)
    {
        if (_killItem is null) return;
        _killItem.Header = engaged ? "Not-Aus ist aktiv (Lösen im Programm)" : "NOT-AUS auslösen";
        _killItem.IsEnabled = !engaged;
        if (_icon is not null)
            _icon.ToolTipText = engaged
                ? "Musterdepot – NOT-AUS AKTIV"
                : "Musterdepot – Paper Trading (kein echtes Geld)";
    }

    /// <summary>Windows-Benachrichtigung (Balloon/Toast über das Tray-Symbol).</summary>
    public void ShowNotification(string title, string message, bool warning = false)
    {
        if (_icon is null) return;
        try
        {
            _icon.ShowNotification(title, message, warning ? NotificationIcon.Warning : NotificationIcon.Info);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Benachrichtigung konnte nicht angezeigt werden");
        }
    }

    private async Task EngageKillSwitchAsync()
    {
        var result = await services.SendAsync(new EngageKillSwitchCommand
        {
            Reason = "Not-Aus über das Symbol im Infobereich",
            Source = CommandSource.Human,
            ClientId = "ui",
        });
        if (result.Success)
            ShowNotification("Not-Aus ausgelöst", "Alle offenen Orders werden storniert. Lösen nur im Programm unter „KI-Aktivität“.", warning: true);
        else
            dialogs.Error("Not-Aus", "Not-Aus konnte nicht ausgelöst werden: " + result.Message);
    }

    public void Dispose()
    {
        _icon?.Dispose();
        _icon = null;
    }
}
