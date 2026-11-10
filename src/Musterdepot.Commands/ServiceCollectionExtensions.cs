using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Commands;
using Musterdepot.Core.Domain;
using Musterdepot.Core.ReadModels;

namespace Musterdepot.Commands;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registriert ICommandBus, ITradingQueries, IReadModels, IApprovalService, IClientRegistry, IMusterdepotRuntime,
    /// AlertMonitor und CommandOptions (alle als Singletons). IUserNotifier, IClock und IIdGenerator nur, falls noch
    /// nicht registriert. ITradingEngine, IMarketDataHub und die Stores kommen aus den anderen Extensions.
    /// </summary>
    public static IServiceCollection AddMusterdepotCommands(this IServiceCollection services)
    {
        services.AddLogging();
        services.TryAddSingleton(new CommandOptions());
        services.TryAddSingleton<IClock>(SystemClock.Instance);
        services.TryAddSingleton<IIdGenerator, GuidIdGenerator>();
        services.TryAddSingleton<IUserNotifier>(NullUserNotifier.Instance);

        // Alle Schreibzugriffe auf die Einstellungen (auch von MCP-TokenService oder App über ISettingsStore)
        // laufen durch den SettingsService, damit Cache und Changed-Ereignis stimmen.
        var inner = services.LastOrDefault(d => d.ServiceType == typeof(ISettingsStore));
        if (inner is not null && inner.ImplementationType != typeof(SettingsStoreAdapter))
        {
            services.Remove(inner);
            services.TryAddSingleton(sp => new SettingsService(CreateFromDescriptor<ISettingsStore>(sp, inner)));
            services.AddSingleton<ISettingsStore, SettingsStoreAdapter>();
        }
        services.TryAddSingleton<SettingsService>();
        services.TryAddSingleton<StoreChangeNotifier>();
        services.TryAddSingleton<ClientActivityTracker>();

        services.TryAddSingleton<ClientRegistry>();
        services.TryAddSingleton<IClientRegistry>(sp => sp.GetRequiredService<ClientRegistry>());

        services.TryAddSingleton<ApprovalQueue>();
        services.TryAddSingleton<IApprovalService>(sp => sp.GetRequiredService<ApprovalQueue>());

        services.TryAddSingleton<CommandBus>();
        services.TryAddSingleton<ICommandBus>(sp => sp.GetRequiredService<CommandBus>());

        services.TryAddSingleton<TradingQueries>();
        services.TryAddSingleton<ITradingQueries>(sp => sp.GetRequiredService<TradingQueries>());

        services.TryAddSingleton<ReadModels>();
        services.TryAddSingleton<IReadModels>(sp => sp.GetRequiredService<ReadModels>());

        services.TryAddSingleton<AlertMonitor>();

        services.TryAddSingleton<MusterdepotRuntime>();
        services.TryAddSingleton<IMusterdepotRuntime>(sp => sp.GetRequiredService<MusterdepotRuntime>());
        return services;
    }

    private static T CreateFromDescriptor<T>(IServiceProvider sp, ServiceDescriptor d) where T : class
    {
        if (d.ImplementationInstance is T instance) return instance;
        if (d.ImplementationFactory is not null) return (T)d.ImplementationFactory(sp);
        if (d.ImplementationType is not null) return (T)ActivatorUtilities.CreateInstance(sp, d.ImplementationType);
        throw new InvalidOperationException($"ISettingsStore-Registrierung {d} kann nicht aufgelöst werden.");
    }
}

/// <summary>ISettingsStore, der über den <see cref="SettingsService"/> (Cache + Changed) läuft.</summary>
public sealed class SettingsStoreAdapter(SettingsService settings) : ISettingsStore
{
    public Task<AppSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(settings.Current);
    public Task SaveAsync(AppSettings settings1, CancellationToken ct = default) => settings.SaveAsync(settings1, ct);
}
