using Microsoft.Extensions.DependencyInjection;

namespace Musterdepot.Engine;

public static class ServiceCollectionExtensions
{
    /// <summary>Registriert ITradingEngine, ITradingCalendar und Gebührenmodelle.</summary>
    public static IServiceCollection AddMusterdepotEngine(this IServiceCollection services)
    {
        // TODO Workstream 2
        return services;
    }
}
