using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Musterdepot.App.Services;
using Musterdepot.Charting;
using Musterdepot.Core.Domain;

namespace Musterdepot.App.ViewModels;

public sealed record ConnectionItem(string Name, string State, bool IsOk, bool IsProblem);

/// <summary>Hauptfenster: Navigation links, Kopfzeile mit Depotwert, Verbindungen, Not-Aus-Zustand.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly IUiDispatcher _ui;
    private readonly List<IDisposable> _subscriptions = [];

    public MainViewModel(
        AppServices services,
        IUiDispatcher ui,
        SelectionService selection,
        RuntimeController runtime,
        ApprovalPopupService approvals,
        WatchlistViewModel watchlist,
        ChartViewModel chart,
        OrderTicketViewModel orderTicket,
        PortfolioViewModel portfolio,
        OrdersViewModel orders,
        AiActivityViewModel aiActivity,
        SettingsViewModel settings)
    {
        _services = services;
        _ui = ui;
        Runtime = runtime;
        Approvals = approvals.Pending;
        Watchlist = watchlist;
        Chart = chart;
        OrderTicket = orderTicket;
        Portfolio = portfolio;
        Orders = orders;
        AiActivity = aiActivity;
        Settings = settings;
        Pages = [watchlist, chart, orderTicket, portfolio, orders, aiActivity, settings];
        _selectedPage = watchlist;
        watchlist.IsActive = true;

        selection.NavigationRequested += (_, page) => SelectedPage = Pages.First(p => p.Page == page);
        Approvals.CollectionChanged += (_, _) => OnPropertyChanged(nameof(PendingApprovalCount));

        MissingServicesText = services.IsComplete
            ? null
            : "Eingeschränkter Betrieb – noch nicht verfügbar: " + string.Join(", ", services.Missing.Select(m => m.Split(' ')[0]));
    }

    public IReadOnlyList<PageViewModel> Pages { get; }
    public WatchlistViewModel Watchlist { get; }
    public ChartViewModel Chart { get; }
    public OrderTicketViewModel OrderTicket { get; }
    public PortfolioViewModel Portfolio { get; }
    public OrdersViewModel Orders { get; }
    public AiActivityViewModel AiActivity { get; }
    public SettingsViewModel Settings { get; }
    public RuntimeController Runtime { get; }
    public ObservableCollection<ApprovalItemViewModel> Approvals { get; }
    public ObservableCollection<ConnectionItem> Connections { get; } = [];

    public int PendingApprovalCount => Approvals.Count;

    public string? MissingServicesText { get; }

    [ObservableProperty] private PageViewModel _selectedPage;
    [ObservableProperty] private string _equity = "–";
    [ObservableProperty] private string _dayPnl = "–";
    [ObservableProperty] private int _dayPnlSign;
    [ObservableProperty] private string _cash = "–";
    [ObservableProperty] private bool _killSwitchEngaged;
    [ObservableProperty] private bool _dailyLossLimitHit;

    partial void OnSelectedPageChanged(PageViewModel? oldValue, PageViewModel newValue)
    {
        if (oldValue is not null) oldValue.IsActive = false;
        newValue.IsActive = true;
    }

    public void Initialize()
    {
        foreach (var p in Pages) p.Initialize();

        if (_services.ReadModels is not { } rm) return;
        _subscriptions.Add(rm.Portfolio.Subscribe(_ui, p =>
        {
            Equity = DisplayFormat.Money(p.Equity, p.BaseCurrency);
            Cash = DisplayFormat.Money(p.Cash, p.BaseCurrency);
            DayPnl = DisplayFormat.SignedMoney(p.DayPnl, p.BaseCurrency);
            DayPnlSign = Math.Sign(p.DayPnl);
            DailyLossLimitHit = p.DailyLossLimitHit;
        }, TimeSpan.FromMilliseconds(250)));
        _subscriptions.Add(rm.KillSwitch.Subscribe(_ui, v => KillSwitchEngaged = v));
        _subscriptions.Add(rm.Connections.Subscribe(_ui, states =>
        {
            Connections.Clear();
            foreach (var (name, state) in states.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase))
                Connections.Add(new ConnectionItem(name, Labels.Connection(state), state == ConnectionState.Connected,
                    state is ConnectionState.Failed or ConnectionState.Disconnected));
        }, TimeSpan.FromMilliseconds(500)));
    }

    [RelayCommand]
    private void Navigate(AppPage page) => SelectedPage = Pages.First(p => p.Page == page);
}
