using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using Microsoft.Web.WebView2.Core;
using Musterdepot.App.ViewModels;
using Musterdepot.Charting;
using Musterdepot.Core.Abstractions;

namespace Musterdepot.App.Views;

/// <summary>
/// Hostet Lightweight Charts in WebView2. Die Seite wird über einen virtuellen Host aus Assets/Chart geladen
/// (kein Netzwerkzugriff nötig). C# → JS: PostWebMessageAsJson; JS → C#: chrome.webview.postMessage.
/// </summary>
public partial class ChartView : UserControl
{
    private const string VirtualHost = "chart.musterdepot.example";
    private ChartViewModel? _vm;
    private bool _initStarted;
    private bool _ready;

    public ChartView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.NewValue as ChartViewModel);
        IsVisibleChanged += async (_, e) =>
        {
            if (e.NewValue is true) await InitializeWebViewAsync();
        };
    }

    private void Attach(ChartViewModel? vm)
    {
        if (_vm is not null) _vm.MessageReady -= OnMessageReady;
        _vm = vm;
        if (_vm is not null) _vm.MessageReady += OnMessageReady;
    }

    private async Task InitializeWebViewAsync()
    {
        if (_initStarted) return;
        _initStarted = true;
        try
        {
            var userData = Path.Combine(AppPaths.DataDirectory, "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: userData);
            await WebView.EnsureCoreWebView2Async(env);
            WebView.DefaultBackgroundColor = System.Drawing.Color.Transparent;

            var core = WebView.CoreWebView2;
            core.Settings.AreDevToolsEnabled = Debugger.IsAttached;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = Debugger.IsAttached;

            var folder = Path.Combine(AppContext.BaseDirectory, "Assets", "Chart");
            core.SetVirtualHostNameToFolderMapping(VirtualHost, folder, CoreWebView2HostResourceAccessKind.Deny);
            core.WebMessageReceived += OnWebMessageReceived;
            core.NavigationStarting += OnNavigationStarting;
            core.NewWindowRequested += OnNewWindowRequested;
            core.ProcessFailed += (_, e) =>
            {
                _ready = false;
                _vm?.OnViewDetached();
                _vm?.OnViewError($"Chart-Prozess beendet ({e.ProcessFailedKind}). „Neu laden“ versuchen.");
                if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited) core.Reload();
            };
            WebView.Source = new Uri($"https://{VirtualHost}/chart.html");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            _vm?.OnViewError("Die WebView2-Laufzeit ist nicht installiert.");
        }
        catch (Exception ex)
        {
            _vm?.OnViewError(ex.GetBaseException().Message);
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || !string.Equals(uri.Host, VirtualHost, StringComparison.OrdinalIgnoreCase))
        {
            // Externe Links (z. B. TradingView-Logo) im Standardbrowser öffnen, nie im Chart-Fenster.
            e.Cancel = true;
            OpenExternal(e.Uri);
            return;
        }
        _ready = false;
        _vm?.OnViewDetached();
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        OpenExternal(e.Uri);
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            switch (type)
            {
                case "ready":
                    _ready = true;
                    _vm?.OnViewReady();
                    break;
                case "error":
                    _vm?.OnViewError(root.TryGetProperty("message", out var m) ? m.GetString() ?? "Unbekannter Fehler" : "Unbekannter Fehler");
                    break;
            }
        }
        catch (JsonException)
        {
            // Unerwartete Nachricht ignorieren.
        }
    }

    private void OnMessageReady(object? sender, ChartMessage message)
    {
        if (!_ready || WebView.CoreWebView2 is null) return;
        try
        {
            WebView.CoreWebView2.PostWebMessageAsJson(message.ToJson());
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            _ready = false;
        }
    }

    private void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        OpenExternal(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    private static void OpenExternal(string? url)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Kein Standardbrowser – ignorieren.
        }
    }
}
