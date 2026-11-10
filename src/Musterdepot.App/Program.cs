using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Musterdepot.App.Hosting;
using Velopack;

namespace Musterdepot.App;

/// <summary>
/// Composition Root. Reihenfolge: Velopack (Installer-Hooks) → Single-Instance → Settings-Bootstrap → Generic Host → WPF.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Velopack muss als Allererstes laufen: Install-/Update-Hooks beenden den Prozess ggf. sofort.
        VelopackApp.Build().Run();

        using var instance = new SingleInstanceGuard("Musterdepot");
        if (!instance.IsFirstInstance)
        {
            instance.SignalFirstInstance();
            return 0;
        }

        var startMinimized = args.Any(a => string.Equals(a, StartupOptions.MinimizedArgument, StringComparison.OrdinalIgnoreCase));

        IHost host;
        BootstrapResult bootstrap;
        try
        {
            bootstrap = SettingsBootstrap.Load();
            host = AppHost.Build(args, bootstrap, new StartupOptions(startMinimized));
        }
        catch (Exception ex)
        {
            FileLoggerProvider.WriteEmergency("Start fehlgeschlagen", ex);
            MessageBox.Show(
                "Musterdepot konnte nicht gestartet werden:\n\n" + ex.Message +
                "\n\nDetails stehen in der Protokolldatei unter %LOCALAPPDATA%\\Musterdepot\\logs.",
                "Musterdepot", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }

        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Musterdepot");
        logger.LogInformation("Musterdepot {Version} startet (Einstellungen aus {Origin}).",
            typeof(Program).Assembly.GetName().Version, bootstrap.Origin);

        try
        {
            host.Start();
            var app = new App(host);
            app.InitializeComponent();
            instance.ListenForActivation(() => app.Dispatcher.InvokeAsync(app.ShowMainWindow));
            return app.Run();
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Unbehandelter Fehler beim Start");
            MessageBox.Show("Unerwarteter Fehler:\n\n" + ex.Message, "Musterdepot", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
        finally
        {
            try
            {
                // Auf dem Thread-Pool, damit keine Fortsetzung auf den (beendeten) Dispatcher wartet.
                Task.Run(async () =>
                {
                    await host.StopAsync(TimeSpan.FromSeconds(5));
                    // Asynchron entsorgen: IMusterdepotRuntime ist nur IAsyncDisposable.
                    if (host is IAsyncDisposable ad) await ad.DisposeAsync();
                    else host.Dispose();
                }).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Fehler beim Beenden des Hosts");
            }
            FileLoggerProvider.FlushAll();
        }
    }
}
