using Microsoft.Extensions.DependencyInjection;

namespace Musterdepot.Commands;

public static class ServiceCollectionExtensions
{
    /// <summary>Registriert ICommandBus, ITradingQueries, IReadModels, IApprovalService, IClientRegistry, IMusterdepotRuntime.</summary>
    public static IServiceCollection AddMusterdepotCommands(this IServiceCollection services)
    {
        // TODO Workstream 3a
        return services;
    }
}
