using CommunityToolkit.Mvvm.ComponentModel;

namespace Musterdepot.App.Services;

/// <summary>Gemeinsam gewähltes Symbol (Watchlist → Chart → Orderticket) und Navigationswünsche.</summary>
public sealed partial class SelectionService : ObservableObject
{
    [ObservableProperty]
    private string? _selectedSymbol;

    /// <summary>Bitte, zu einer Seite zu wechseln (wird vom MainViewModel ausgeführt).</summary>
    public event EventHandler<AppPage>? NavigationRequested;

    public void RequestNavigation(AppPage page) => NavigationRequested?.Invoke(this, page);

    public void Select(string symbol, AppPage? navigateTo = null)
    {
        SelectedSymbol = symbol;
        if (navigateTo is { } p) RequestNavigation(p);
    }
}

public enum AppPage { Watchlist, Chart, OrderTicket, Portfolio, Orders, AiActivity, Settings }
