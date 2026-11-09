using Microsoft.Extensions.DependencyInjection;

namespace Musterdepot.Mcp;

public static class ServiceCollectionExtensions
{
    /// <summary>Registriert IMcpServerHost (Kestrel auf 127.0.0.1) und ITokenService.</summary>
    public static IServiceCollection AddMusterdepotMcp(this IServiceCollection services)
    {
        // TODO Workstream 3b
        return services;
    }
}
