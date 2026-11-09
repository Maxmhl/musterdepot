using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Commands;
using Musterdepot.Core.ReadModels;

namespace Musterdepot.Mcp.Tools;

/// <summary>
/// Zugriff der Tools auf die App-Singletons (gleiche Engine-Instanz wie die Oberfläche).
/// Die Dienste werden bei jedem Zugriff aus dem Root-<see cref="IServiceProvider"/> der App aufgelöst,
/// damit HTTP-Server (eigener Kestrel-Container) und Pipe-Brücke exakt dieselben Instanzen verwenden.
/// </summary>
public sealed class McpToolBackend(IServiceProvider root)
{
    public ICommandBus CommandBus => root.GetRequiredService<ICommandBus>();
    public ITradingQueries Queries => root.GetRequiredService<ITradingQueries>();
    public IAuditLog AuditLog => root.GetRequiredService<IAuditLog>();
    public IClock Clock => root.GetService<IClock>() ?? SystemClock.Instance;
    public ILogger Logger => root.GetService<ILoggerFactory>()?.CreateLogger("Musterdepot.Mcp.Tools") ?? NullLogger.Instance;
}
