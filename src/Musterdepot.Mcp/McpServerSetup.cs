using System.Reflection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Musterdepot.Mcp.Tools;

namespace Musterdepot.Mcp;

/// <summary>Gemeinsame Server-Konfiguration für HTTP und Pipe-Brücke (gleiche Tools, gleiche Beschreibung).</summary>
public static class McpServerSetup
{
    public const string Instructions =
        "Musterdepot ist ein Paper-Trading-Programm (Spielgeld, keine echte Börse) mit Basiswährung EUR. " +
        "Krypto-Kurse sind Echtzeit, Aktienkurse 15–30 Minuten verzögert – achte auf 'delayed_minutes', 'as_of_utc' und 'is_realtime'. " +
        "Symbole: Krypto z. B. 'BTC/EUR', Aktien per ISIN; im Zweifel zuerst search_symbol. " +
        "Vor dem Handeln get_agent_status aufrufen (Modus, Limits, Not-Aus). Orders mit place_order immer mit eigenem, eindeutigem " +
        "idempotency_key; mit dry_run=true vorher Gebühren und Risikoprüfung ansehen. Status 'pending_approval' heißt: Ein Mensch muss " +
        "im Programm freigeben. Risiko-Limits und Modi kann nur der Mensch ändern. Bei Problemen engage_kill_switch auslösen.";

    private static readonly Lazy<IReadOnlyList<McpServerTool>> s_tools = new(MusterdepotTools.CreateTools);

    /// <summary>Die (zustandslosen) Tool-Objekte, einmal pro Prozess erzeugt.</summary>
    public static IReadOnlyList<McpServerTool> Tools => s_tools.Value;

    public static Implementation ServerInfo(McpOptions options) => new()
    {
        Name = options.ServerName,
        Title = "Musterdepot (Paper-Trading)",
        Version = typeof(McpServerSetup).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.1.0",
    };

    /// <summary>Server-Optionen für einen Stream-Transport (Pipe-Brücke).</summary>
    public static McpServerOptions CreateStreamServerOptions(McpOptions options)
    {
        var collection = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var tool in Tools) collection.Add(tool);
        return new McpServerOptions
        {
            ServerInfo = ServerInfo(options),
            ServerInstructions = Instructions,
            ToolCollection = collection,
        };
    }
}
