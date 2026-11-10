using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Musterdepot.App.Services;

namespace Musterdepot.App.ViewModels;

/// <summary>Basis für die sieben Bildschirme.</summary>
public abstract partial class PageViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _statusIsError;

    public abstract AppPage Page { get; }
    public abstract string Title { get; }
    /// <summary>Symbol aus "Segoe Fluent Icons"/"Segoe MDL2 Assets".</summary>
    public abstract string Glyph { get; }

    /// <summary>Einmalig nach dem Start: Lesemodelle abonnieren.</summary>
    public virtual void Initialize() { }

    partial void OnIsActiveChanged(bool value)
    {
        if (value) OnActivated();
    }

    protected virtual void OnActivated() { }

    protected void SetStatus(string? message, bool isError = false)
    {
        StatusMessage = message;
        StatusIsError = isError;
    }
}

/// <summary>Zahleneingaben in der Oberfläche: deutsche und internationale Schreibweise, leer = kein Wert.</summary>
public static class NumberInput
{
    public static bool TryParse(string? text, out decimal? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        var t = text.Trim().Replace("€", "").Replace("%", "").Replace(" ", "").Replace(" ", "");
        if (decimal.TryParse(t, NumberStyles.Number, CultureInfo.CurrentCulture, out var d) ||
            decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out d))
        {
            value = d;
            return true;
        }
        return false;
    }

    public static decimal? ParseOrNull(string? text) => TryParse(text, out var v) ? v : null;

    public static string Format(decimal? value) =>
        value is { } d ? d.ToString("0.##########", CultureInfo.CurrentCulture) : "";

    public static string Format(int value) => value.ToString(CultureInfo.CurrentCulture);
}
