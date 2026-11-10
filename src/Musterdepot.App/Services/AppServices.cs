using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Commands;
using Musterdepot.Core.ReadModels;

namespace Musterdepot.App.Services;

/// <summary>
/// Löst die Core-Schnittstellen der anderen Workstreams zur Laufzeit auf. Fehlt ein Dienst (noch nicht gemergt) oder
/// scheitert seine Erzeugung, ist die Eigenschaft <c>null</c> und <see cref="Missing"/> nennt ihn – die Oberfläche
/// läuft eingeschränkt weiter, statt abzustürzen.
/// </summary>
public sealed class AppServices
{
    private readonly List<string> _missing = [];

    public AppServices(IServiceProvider sp, ILogger<AppServices> logger)
    {
        T? Resolve<T>(string description) where T : class
        {
            try
            {
                var s = sp.GetService<T>();
                if (s is null) _missing.Add($"{typeof(T).Name} – {description} (nicht registriert)");
                return s;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Dienst {Service} konnte nicht erzeugt werden", typeof(T).Name);
                _missing.Add($"{typeof(T).Name} – {description} (Fehler: {ex.GetBaseException().Message})");
                return null;
            }
        }

        CommandBus = Resolve<ICommandBus>("Befehle, Orders");
        ReadModels = Resolve<IReadModels>("Lesemodelle (Kurse, Depot, Orders)");
        Queries = Resolve<ITradingQueries>("Abfragen (Kerzen, Suche, Statistik)");
        Runtime = Resolve<IMusterdepotRuntime>("Laufzeit (Marktdaten → Engine)");
        ApprovalService = Resolve<IApprovalService>("Freigabe-Warteschlange");
        ClientRegistry = Resolve<IClientRegistry>("KI-Clients");
        McpHost = Resolve<IMcpServerHost>("MCP-Server");
        TokenService = Resolve<ITokenService>("Tokens für KI-Clients");

        if (_missing.Count > 0)
            logger.LogWarning("Eingeschränkter Betrieb, fehlende Dienste: {Missing}", string.Join("; ", _missing));
    }

    public ICommandBus? CommandBus { get; }
    public IReadModels? ReadModels { get; }
    public ITradingQueries? Queries { get; }
    public IMusterdepotRuntime? Runtime { get; }
    public IApprovalService? ApprovalService { get; }
    public IClientRegistry? ClientRegistry { get; }
    public IMcpServerHost? McpHost { get; }
    public ITokenService? TokenService { get; }

    public IReadOnlyList<string> Missing => _missing;
    public bool IsComplete => _missing.Count == 0;

    /// <summary>Sendet einen Befehl; ohne Befehlsschicht gibt es eine verständliche Fehlermeldung statt einer Ausnahme.</summary>
    public async Task<CommandResult> SendAsync(ICommand command, CancellationToken ct = default)
    {
        if (CommandBus is null)
            return CommandResult.Fail(CommandStatus.Failed, "Die Befehlsschicht (ICommandBus) ist nicht verfügbar.");
        try
        {
            return await CommandBus.SendAsync(command, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CommandResult.Fail(CommandStatus.Failed, ex.GetBaseException().Message);
        }
    }
}
