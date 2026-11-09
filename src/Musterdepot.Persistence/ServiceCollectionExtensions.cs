using Microsoft.Extensions.DependencyInjection;

namespace Musterdepot.Persistence;

public static class ServiceCollectionExtensions
{
    /// <summary>Registriert IEventStore, ICandleStore, IAuditLog, ISettingsStore, IWatchlistStore, IAlertStore, IInstrumentStore (SQLite).</summary>
    public static IServiceCollection AddMusterdepotPersistence(this IServiceCollection services, string databasePath)
    {
        // TODO Workstream 3a
        return services;
    }
}
