using Microsoft.Extensions.DependencyInjection;
using Musterdepot.Core.Domain;

namespace Musterdepot.MarketData;

public static class ServiceCollectionExtensions
{
    /// <summary>Registriert IMarketDataHub, IFxRateProvider und die aktivierten Quellen.</summary>
    public static IServiceCollection AddMusterdepotMarketData(this IServiceCollection services, DataSourceSettings settings)
    {
        // TODO Workstream 1
        return services;
    }
}
