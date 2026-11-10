using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Musterdepot.App.Hosting;
using Musterdepot.App.Services;
using Musterdepot.App.ViewModels;
using Musterdepot.App.Views;
using Musterdepot.Core.Domain;

namespace Musterdepot.App;

public partial class App : Application
{
    private readonly IHost _host;
    private readonly ILogger _logger;
    private MainWindow? _mainWindow;
    private bool _exiting;
    private bool _trayHintShown;
    private readonly List<IDisposable> _subscriptions = [];

    public App(IHost host)
    {
        _host = host;
        AppServiceLocator.Initialize(host.Services);
        _logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Musterdepot.App");

        // WPF formatiert Bindungen sonst immer als en-US.
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag)));

        // Fluent-Theme vor dem Laden der Ressourcen setzen, damit eigene Stile darauf aufbauen.
        var theme = host.Services.GetRequiredService<BootstrapResult>().Settings.Theme;
        try
        {
            ThemeMode = theme?.ToLowerInvariant() switch
            {
                "dark" => ThemeMode.Dark,
                "light" => ThemeMode.Light,
                _ => ThemeService.SystemUsesDarkTheme() ? ThemeMode.Dark : ThemeMode.Light,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fluent-Theme nicht verfügbar");
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            _logger.LogCritical(e.ExceptionObject as Exception, "Unbehandelte Ausnahme (Prozess)");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _logger.LogError(e.Exception, "Unbeobachtete Task-Ausnahme");
            e.SetObserved();
        };
    }

    private IServiceProvider Services => _host.Services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var bootstrap = Services.GetRequiredService<BootstrapResult>();
        var startup = Services.GetRequiredService<StartupOptions>();
        var appServices = Services.GetRequiredService<AppServices>();
        var theme = Services.GetRequiredService<ThemeService>();
        var tray = Services.GetRequiredService<TrayService>();
        var runtime = Services.GetRequiredService<RuntimeController>();

        theme.Apply(bootstrap.Settings.Theme);

        tray.Create();
        tray.OpenRequested += (_, _) => ShowMainWindow();
        tray.ExitRequested += async (_, _) => await ExitAsync();

        Services.GetRequiredService<ApprovalPopupService>().Attach();
        Services.GetRequiredService<PowerEventsService>().Attach();

        var ui = Services.GetRequiredService<IUiDispatcher>();
        if (appServices.ReadModels is { } rm)
        {
            _subscriptions.Add(rm.KillSwitch.Subscribe(ui, tray.SetKillSwitchState));
            _subscriptions.Add(rm.Settings.Subscribe(ui, s =>
            {
                SettingsBootstrap.SaveCache(s);
                if (!string.Equals(s.Theme, theme.Preference, StringComparison.OrdinalIgnoreCase)) theme.Apply(s.Theme);
            }, TimeSpan.FromMilliseconds(500)));
        }

        var mainVm = Services.GetRequiredService<MainViewModel>();
        _mainWindow = new MainWindow(Services.GetRequiredService<ThemeService>()) { DataContext = mainVm };
        _mainWindow.Closing += OnMainWindowClosing;
        MainWindow = _mainWindow;
        mainVm.Initialize();

        if (startup.StartMinimized && tray.IsCreated)
        {
            tray.ShowNotification("Musterdepot", "Läuft im Infobereich. Doppelklick zum Öffnen.");
        }
        else
        {
            _mainWindow.Show();
        }

        if (!appServices.IsComplete)
        {
            var dialogs = Services.GetRequiredService<IDialogService>();
            Dispatcher.InvokeAsync(() => dialogs.Info("Eingeschränkter Betrieb",
                "Einige Programmteile sind in dieser Version noch nicht verfügbar:\n\n• " +
                string.Join("\n• ", appServices.Missing) +
                "\n\nDie Oberfläche startet trotzdem; betroffene Funktionen sind deaktiviert."), DispatcherPriority.ApplicationIdle);
        }

        _ = runtime.StartAsync();
    }

    /// <summary>Hauptfenster zeigen (Tray, zweite Instanz).</summary>
    public void ShowMainWindow()
    {
        if (_mainWindow is null || _exiting) return;
        if (!_mainWindow.IsVisible) _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized) _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
        _mainWindow.Topmost = true;   // zuverlässig nach vorne holen
        _mainWindow.Topmost = false;
        _mainWindow.Focus();
    }

    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        var settings = Services.GetRequiredService<AppServices>().ReadModels?.Settings.Current
                       ?? Services.GetRequiredService<BootstrapResult>().Settings;
        var tray = Services.GetRequiredService<TrayService>();
        if (settings.MinimizeToTray && tray.IsCreated)
        {
            // Schließen minimiert in den Infobereich; Datenverbindungen und MCP-Server laufen weiter.
            e.Cancel = true;
            _mainWindow?.Hide();
            if (!_trayHintShown)
            {
                tray.ShowNotification("Musterdepot läuft weiter",
                    "Kurse, Orders und MCP-Server laufen im Hintergrund. Beenden über das Symbol im Infobereich.");
                _trayHintShown = true;
            }
            return;
        }
        e.Cancel = true;
        _ = ExitAsync();
    }

    /// <summary>Sauber beenden: MCP und Laufzeit stoppen, Tray entfernen, Anwendung schließen.</summary>
    public async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _logger.LogInformation("Musterdepot wird beendet");
        try
        {
            await Services.GetRequiredService<RuntimeController>().ShutdownAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fehler beim Beenden");
        }
        foreach (var s in _subscriptions) s.Dispose();
        Services.GetRequiredService<PowerEventsService>().Dispose();
        Services.GetRequiredService<ThemeService>().Detach();
        Services.GetRequiredService<TrayService>().Dispose();
        _mainWindow?.Close();
        Shutdown();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger.LogError(e.Exception, "Unbehandelte Ausnahme im UI-Thread");
        e.Handled = true;
        try
        {
            MessageBox.Show("Ein unerwarteter Fehler ist aufgetreten und wurde protokolliert:\n\n" + e.Exception.GetBaseException().Message,
                "Musterdepot", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception)
        {
        }
    }
}
