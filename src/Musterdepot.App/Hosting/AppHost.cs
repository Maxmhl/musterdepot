using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Musterdepot.App.Services;
using Musterdepot.App.ViewModels;
using Musterdepot.Commands;
using Musterdepot.Core.Abstractions;
using Musterdepot.Engine;
using Musterdepot.MarketData;
using Musterdepot.Mcp;
using Musterdepot.Persistence;

namespace Musterdepot.App.Hosting;

/// <summary>Baut den Generic Host mit allen Workstream-Registrierungen und den Diensten der Oberfläche.</summary>
public static class AppHost
{
    public static IHost Build(string[] args, BootstrapResult bootstrap, StartupOptions startup)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
            ApplicationName = "Musterdepot",
        });

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddDebug();
        builder.Logging.AddProvider(new FileLoggerProvider(AppPaths.LogsDirectory));

        var settings = bootstrap.Settings;
        var services = builder.Services;
        services.AddSingleton(bootstrap);
        services.AddSingleton(startup);

        // ---- Workstreams 1–3 (registrieren, solange nicht gemergt, ggf. noch nichts) ----
        services.AddMusterdepotPersistence(AppPaths.DatabasePath);
        services.AddMusterdepotMarketData(settings.DataSources);
        services.AddMusterdepotEngine();
        services.AddMusterdepotCommands();
        services.AddMusterdepotMcp();

        // ---- Oberfläche ----
        services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();
        services.AddSingleton<AppServices>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<SelectionService>();
        services.AddSingleton<IDialogService, WpfDialogService>();
        services.AddSingleton<AutostartService>();
        services.AddSingleton<TrayService>();
        services.AddSingleton<ApprovalPopupService>();
        services.AddSingleton<PowerEventsService>();
        services.AddSingleton<RuntimeController>();

        // Eigene Benachrichtigung ersetzt eine eventuelle Standard-Implementierung (z. B. "nichts tun") der Befehlsschicht.
        services.RemoveAll<IUserNotifier>();
        services.AddSingleton<WpfUserNotifier>();
        services.AddSingleton<IUserNotifier>(sp => sp.GetRequiredService<WpfUserNotifier>());

        services.AddSingleton<MainViewModel>();
        services.AddSingleton<WatchlistViewModel>();
        services.AddSingleton<ChartViewModel>();
        services.AddSingleton<OrderTicketViewModel>();
        services.AddSingleton<PortfolioViewModel>();
        services.AddSingleton<OrdersViewModel>();
        services.AddSingleton<AiActivityViewModel>();
        services.AddSingleton<SettingsViewModel>();

        return builder.Build();
    }
}
