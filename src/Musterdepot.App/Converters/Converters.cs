using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Musterdepot.App.Converters;

/// <summary>true → Collapsed, false → Visible.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility v && v != Visibility.Visible;
}

/// <summary>null/leerer Text → Collapsed, sonst Visible. Parameter "invert" kehrt um.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var empty = value is null || value is string s && string.IsNullOrWhiteSpace(s);
        if (parameter is "invert") empty = !empty;
        return empty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Zahl &gt; 0 → Visible (z. B. Anzahl offener Freigaben).</summary>
public sealed class PositiveToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var positive = value switch
        {
            int i => i > 0,
            decimal d => d > 0,
            _ => false,
        };
        if (parameter is "invert") positive = !positive;
        return positive ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Vergleicht einen Enum-Wert mit dem Parameter (für RadioButtons).</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not null && parameter is not null && string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is true && parameter is string s)
        {
            if (targetType.IsEnum) return Enum.Parse(targetType, s);
            if (targetType == typeof(bool) || targetType == typeof(bool?)) return bool.Parse(s);
        }
        return Binding.DoNothing;
    }
}
