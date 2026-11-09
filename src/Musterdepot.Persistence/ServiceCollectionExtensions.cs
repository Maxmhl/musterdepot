using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Musterdepot.Core.Abstractions;

namespace Musterdepot.Persistence;

public static class ServiceCollectionExtensions
{
    /// <summary>Registriert IEventStore, ICandleStore, IAuditLog, ISettingsStore, IWatchlistStore, IAlertStore, IInstrumentStore (SQLite).</summary>
    public static IServiceCollection AddMusterdepotPersistence(this IServiceCollection services, string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        services.AddSingleton(sp => new MusterdepotDatabase(databasePath, sp.GetService<ILogger<MusterdepotDatabase>>()));
        services.AddSingleton<IDbContextFactory<MusterdepotDbContext>>(sp => sp.GetRequiredService<MusterdepotDatabase>());
        services.AddSingleton<IEventStore>(sp => new SqliteEventStore(sp.GetRequiredService<MusterdepotDatabase>()));
        services.AddSingleton<ICandleStore>(sp => new SqliteCandleStore(sp.GetRequiredService<MusterdepotDatabase>(), sp.GetService<IClock>()));
        services.AddSingleton<IAuditLog>(sp => new SqliteAuditLog(sp.GetRequiredService<MusterdepotDatabase>()));
        services.AddSingleton<ISettingsStore>(sp => new SqliteSettingsStore(sp.GetRequiredService<MusterdepotDatabase>()));
        services.AddSingleton<IWatchlistStore>(sp => new SqliteWatchlistStore(sp.GetRequiredService<MusterdepotDatabase>()));
        services.AddSingleton<IAlertStore>(sp => new SqliteAlertStore(sp.GetRequiredService<MusterdepotDatabase>()));
        services.AddSingleton<IInstrumentStore>(sp => new SqliteInstrumentStore(sp.GetRequiredService<MusterdepotDatabase>()));
        return services;
    }

    /// <summary>Standardpfad <see cref="AppPaths.DatabasePath"/> (%LOCALAPPDATA%\Musterdepot\musterdepot.db).</summary>
    public static IServiceCollection AddMusterdepotPersistence(this IServiceCollection services)
        => services.AddMusterdepotPersistence(AppPaths.DatabasePath);
}
