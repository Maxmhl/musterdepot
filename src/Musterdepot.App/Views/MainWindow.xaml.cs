using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Musterdepot.App.Services;

namespace Musterdepot.App.Views;

public partial class MainWindow : Window
{
    private readonly ThemeService _theme;

    public MainWindow(ThemeService theme)
    {
        _theme = theme;
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyTitleBar(_theme.IsDark);
        _theme.ThemeChanged += (_, dark) => ApplyTitleBar(dark);
    }

    /// <summary>Dunkle Titelleiste unter Windows 10 (ab 20H1) und 11.</summary>
    private void ApplyTitleBar(bool dark)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int value = dark ? 1 : 0;
        try
        {
            _ = DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref value, sizeof(int));
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
