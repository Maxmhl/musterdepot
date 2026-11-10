using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Musterdepot.App.Hosting;

namespace Musterdepot.App.Services;

/// <summary>Autostart über HKCU\Software\Microsoft\Windows\CurrentVersion\Run – ohne Adminrechte.</summary>
public sealed class AutostartService(ILogger<AutostartService> logger)
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Musterdepot";

    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Autostart-Eintrag nicht lesbar");
                return false;
            }
        }
    }

    public bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled)
                key.SetValue(ValueName, $"\"{LauncherPath()}\" {StartupOptions.MinimizedArgument}", RegistryValueKind.String);
            else if (key.GetValue(ValueName) is not null)
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            logger.LogInformation("Autostart {State}", enabled ? "eingeschaltet" : "ausgeschaltet");
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Autostart konnte nicht geändert werden");
            return false;
        }
    }

    /// <summary>
    /// Pfad der Exe. Bei Velopack-Installationen liegt die versionierte Exe in "current\"; der Starter eine Ebene
    /// darüber bleibt über Updates hinweg gleich.
    /// </summary>
    private static string LauncherPath()
    {
        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Musterdepot.exe");
        var dir = Path.GetDirectoryName(exe);
        if (dir is not null && string.Equals(Path.GetFileName(dir), "current", StringComparison.OrdinalIgnoreCase))
        {
            var stub = Path.Combine(Path.GetDirectoryName(dir)!, Path.GetFileName(exe));
            if (File.Exists(stub)) return stub;
        }
        return exe;
    }
}
