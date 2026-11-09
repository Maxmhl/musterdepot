using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Musterdepot.Core.Abstractions;
using Musterdepot.Mcp.Bridge;
using Musterdepot.Mcp.Tools;

namespace Musterdepot.Mcp;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registriert <see cref="IMcpServerHost"/> (Kestrel auf 127.0.0.1 + Named-Pipe-Brücke), <see cref="ITokenService"/>,
    /// <see cref="BridgePipeServer"/>, <see cref="McpToolBackend"/> und <see cref="McpOptions"/>.
    /// Erwartet aus anderen Projekten: ICommandBus, ITradingQueries, IAuditLog, ISettingsStore; optional IClientRegistry, IClock, ILoggerFactory.
    /// Start: Die App ruft nach dem Start der Runtime <c>IMcpServerHost.StartAsync()</c> auf (startet HTTP und Pipe) und beim
    /// Beenden <c>StopAsync()</c>. Alternativ <see cref="AddMusterdepotMcpHostedService"/> bei Verwendung des Generic Host.
    /// </summary>
    public static IServiceCollection AddMusterdepotMcp(this IServiceCollection services)
    {
        services.AddOptions<McpOptions>();
        services.TryAddSingleton<McpToolBackend>(sp => new McpToolBackend(sp));
        services.TryAddSingleton<ITokenService>(sp => new TokenService(
            sp.GetRequiredService<ISettingsStore>(),
            sp.GetRequiredService<IOptions<McpOptions>>(),
            sp.GetService<IClientRegistry>(),
            sp.GetService<IClock>(),
            sp.GetService<ILogger<TokenService>>()));
        services.TryAddSingleton<BridgePipeServer>(sp => new BridgePipeServer(
            sp,
            sp.GetRequiredService<IOptions<McpOptions>>(),
            sp.GetRequiredService<ITokenService>(),
            sp.GetRequiredService<ISettingsStore>(),
            sp.GetService<ILoggerFactory>()));
        services.TryAddSingleton<McpServerHost>(sp => new McpServerHost(
            sp,
            sp.GetRequiredService<IOptions<McpOptions>>(),
            sp.GetRequiredService<ISettingsStore>(),
            sp.GetRequiredService<ITokenService>(),
            sp.GetRequiredService<BridgePipeServer>(),
            sp.GetService<ILoggerFactory>()));
        services.TryAddSingleton<IMcpServerHost>(sp => sp.GetRequiredService<McpServerHost>());
        return services;
    }

    /// <summary>Wie <see cref="AddMusterdepotMcp(IServiceCollection)"/>, zusätzlich mit Optionen (Port-Override, EnablePipe, …).</summary>
    public static IServiceCollection AddMusterdepotMcp(this IServiceCollection services, Action<McpOptions> configure)
    {
        services.AddMusterdepotMcp();
        services.Configure(configure);
        return services;
    }

    /// <summary>Optional: startet/stoppt <see cref="IMcpServerHost"/> automatisch mit dem Generic Host.</summary>
    public static IServiceCollection AddMusterdepotMcpHostedService(this IServiceCollection services)
    {
        services.AddHostedService<McpServerHostedService>();
        return services;
    }
}

/// <summary>Startet den MCP-Host mit dem Generic Host (optional).</summary>
public sealed class McpServerHostedService(IMcpServerHost host) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => host.StartAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => host.StopAsync(cancellationToken);
}
