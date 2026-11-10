using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Musterdepot.App.Services;

/// <summary>
/// Hell/Dunkel: setzt das Fluent-Theme von WPF (<see cref="Application.ThemeMode"/>) und tauscht die eigene Farbpalette.
/// "system" folgt der Windows-Einstellung "App-Modus".
/// </summary>
public sealed class ThemeService(ILogger<ThemeService> logger)
{
    private const string PaletteLight = "pack://application:,,,/Musterdepot;component/Themes/Palette.Light.xaml";
    private const string PaletteDark = "pack://application:,,,/Musterdepot;component/Themes/Palette.Dark.xaml";
    private string _preference = "system";
    private bool _listening;

    public bool IsDark { get; private set; }

    /// <summary>Wird nach jedem Wechsel ausgelöst (UI-Thread). Argument: dunkel?</summary>
    public event EventHandler<bool>? ThemeChanged;

    public string Preference => _preference;

    public void Apply(string? preference)
    {
        _preference = string.IsNullOrWhiteSpace(preference) ? "system" : preference.Trim().ToLowerInvariant();
        if (!_listening)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _listening = true;
        }
        Update();
    }

    public void Detach()
    {
        if (_listening) SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _listening = false;
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (_preference == "system" && e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
            Application.Current?.Dispatcher.InvokeAsync(Update);
    }

    private void Update()
    {
        var app = Application.Current;
        if (app is null) return;
        var dark = _preference switch
        {
            "dark" => true,
            "light" => false,
            _ => SystemUsesDarkTheme(),
        };
        IsDark = dark;

        try
        {
            app.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
        }
        catch (Exception ex)
        {
            // Fluent-Theme nicht verfügbar: eigene Palette reicht für ein lesbares Erscheinungsbild.
            logger.LogWarning(ex, "Fluent-Theme konnte nicht gesetzt werden");
        }

        var dictionaries = app.Resources.MergedDictionaries;
        var palette = new ResourceDictionary { Source = new Uri(dark ? PaletteDark : PaletteLight, UriKind.Absolute) };
        var existing = dictionaries.FirstOrDefault(d => d.Source is { } s &&
            (s.OriginalString.EndsWith("Palette.Light.xaml", StringComparison.OrdinalIgnoreCase) ||
             s.OriginalString.EndsWith("Palette.Dark.xaml", StringComparison.OrdinalIgnoreCase)));
        if (existing is not null)
        {
            var index = dictionaries.IndexOf(existing);
            dictionaries[index] = palette;
        }
        else
        {
            dictionaries.Add(palette);
        }

        ThemeChanged?.Invoke(this, dark);
    }

    public static bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
