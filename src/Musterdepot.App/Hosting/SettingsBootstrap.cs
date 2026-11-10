using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;
using Musterdepot.Core.Serialization;
using Musterdepot.Persistence;

namespace Musterdepot.App.Hosting;

public sealed record BootstrapResult(AppSettings Settings, string Origin);

/// <summary>Kommandozeilen-Optionen beim Start.</summary>
public sealed record StartupOptions(bool StartMinimized)
{
    public const string MinimizedArgument = "--minimized";
}

/// <summary>
/// Liest die Einstellungen, bevor der Host gebaut wird (Datenquellen werden bei der Registrierung gebraucht).
/// Reihenfolge: SQLite über <see cref="ISettingsStore"/> → lokale Kopie settings.json → Standardwerte.
/// </summary>
public static class SettingsBootstrap
{
    public static string CachePath => Path.Combine(AppPaths.DataDirectory, "settings.json");

    public static BootstrapResult Load()
    {
        var fromStore = TryLoadFromStore();
        if (fromStore is not null) return new BootstrapResult(fromStore, "Datenbank");

        var fromCache = TryLoadCache();
        if (fromCache is not null) return new BootstrapResult(fromCache, "settings.json");

        return new BootstrapResult(new AppSettings(), "Standardwerte");
    }

    /// <summary>Schreibt eine Kopie der Einstellungen (ohne KI-Client-Daten) für den nächsten Start.</summary>
    public static void SaveCache(AppSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings with { AiClients = [] }, JsonDefaults.Options);
            var tmp = CachePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, CachePath, overwrite: true);
        }
        catch (Exception ex)
        {
            FileLoggerProvider.WriteEmergency("Einstellungs-Kopie konnte nicht geschrieben werden", ex);
        }
    }

    private static AppSettings? TryLoadFromStore()
    {
        try
        {
            return Task.Run(async () =>
            {
                var services = new ServiceCollection();
                services.AddLogging();
                services.AddMusterdepotPersistence(AppPaths.DatabasePath);
                await using var sp = services.BuildServiceProvider();
                var store = sp.GetService<ISettingsStore>();
                if (store is null) return null;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                return await store.LoadAsync(cts.Token);
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            FileLoggerProvider.WriteEmergency("Einstellungen aus der Datenbank nicht lesbar, nutze Kopie/Standard", ex);
            return null;
        }
    }

    private static AppSettings? TryLoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(CachePath), JsonDefaults.Options);
        }
        catch (Exception ex)
        {
            FileLoggerProvider.WriteEmergency("settings.json ist beschädigt", ex);
            return null;
        }
    }
}
