using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Musterdepot.App.Services;
using Musterdepot.Charting;
using Musterdepot.Core.Domain;

namespace Musterdepot.App.ViewModels;

public sealed record StatItem(string Label, string Value, int Sign = 0);

/// <summary>Depot: Positionen, Cash, Equity, Tages-GuV, Equity-Kurve (ScottPlot) und Kennzahlen mit Filter Mensch/KI/Tag.</summary>
public sealed partial class PortfolioViewModel(
    AppServices services,
    IUiDispatcher ui,
    SelectionService selection,
    ILogger<PortfolioViewModel> logger) : PageViewModel
{
    private readonly List<IDisposable> _subscriptions = [];

    public override AppPage Page => AppPage.Portfolio;
    public override string Title => "Depot";
    public override string Glyph => "";

    public ObservableCollection<PositionRowViewModel> Positions { get; } = [];
    public ObservableCollection<StatItem> Stats { get; } = [];

    public IReadOnlyList<Option<CommandSource?>> SourceFilters { get; } =
        [new(null, "Alle"), new(CommandSource.Human, "Mensch"), new(CommandSource.Ai, "KI")];

    /// <summary>Equity-Kurve für ScottPlot (die View zeichnet bei Änderung neu).</summary>
    public IReadOnlyList<EquitySnapshot> EquityCurve { get; private set; } = [];
    public event EventHandler? EquityCurveChanged;

    [ObservableProperty] private string _cash = "–";
    [ObservableProperty] private string _availableCash = "–";
    [ObservableProperty] private string _reservedCash = "–";
    [ObservableProperty] private string _positionsValue = "–";
    [ObservableProperty] private string _equity = "–";
    [ObservableProperty] private string _dayPnl = "–";
    [ObservableProperty] private int _dayPnlSign;
    [ObservableProperty] private string _realizedPnl = "–";
    [ObservableProperty] private int _realizedSign;
    [ObservableProperty] private string _unrealizedPnl = "–";
    [ObservableProperty] private int _unrealizedSign;
    [ObservableProperty] private string _feesPaid = "–";
    [ObservableProperty] private string _asOf = "";
    [ObservableProperty] private bool _hasDelayedPrices;
    [ObservableProperty] private bool _killSwitchEngaged;
    [ObservableProperty] private bool _dailyLossLimitHit;
    [ObservableProperty] private PositionRowViewModel? _selectedPosition;
    [ObservableProperty] private Option<CommandSource?>? _selectedSourceFilter;
    [ObservableProperty] private string _tagFilter = "";

    public override void Initialize()
    {
        SelectedSourceFilter = SourceFilters[0];
        if (services.ReadModels is not { } rm)
        {
            SetStatus("Depotdaten nicht verfügbar: Lesemodelle fehlen.", true);
            return;
        }
        _subscriptions.Add(rm.Portfolio.Subscribe(ui, Apply, TimeSpan.FromMilliseconds(300)));
        _subscriptions.Add(rm.EquityCurve.Subscribe(ui, c =>
        {
            EquityCurve = c ?? [];
            EquityCurveChanged?.Invoke(this, EventArgs.Empty);
        }, TimeSpan.FromSeconds(1)));
        _subscriptions.Add(rm.Fills.Subscribe(ui, _ => { if (IsActive) RefreshStats(); }, TimeSpan.FromSeconds(1)));
    }

    protected override void OnActivated() => RefreshStats();

    private void Apply(PortfolioSnapshot? p)
    {
        if (p is null) return;
        var cur = p.BaseCurrency;
        Cash = DisplayFormat.Money(p.Cash, cur);
        AvailableCash = DisplayFormat.Money(p.AvailableCash, cur);
        ReservedCash = DisplayFormat.Money(p.ReservedCash, cur);
        PositionsValue = DisplayFormat.Money(p.PositionsValue, cur);
        Equity = DisplayFormat.Money(p.Equity, cur);
        DayPnl = DisplayFormat.SignedMoney(p.DayPnl, cur) + (p.DayStartEquity != 0 ? $" ({DisplayFormat.Percent(p.DayPnl / p.DayStartEquity * 100m)})" : "");
        DayPnlSign = Math.Sign(p.DayPnl);
        RealizedPnl = DisplayFormat.SignedMoney(p.RealizedPnl, cur);
        RealizedSign = Math.Sign(p.RealizedPnl);
        UnrealizedPnl = DisplayFormat.SignedMoney(p.UnrealizedPnl, cur);
        UnrealizedSign = Math.Sign(p.UnrealizedPnl);
        FeesPaid = DisplayFormat.Money(p.FeesPaid, cur);
        AsOf = $"Stand {p.AsOfUtc.ToLocalTime():dd.MM.yyyy HH:mm:ss}";
        KillSwitchEngaged = p.KillSwitchEngaged;
        DailyLossLimitHit = p.DailyLossLimitHit;

        var selected = SelectedPosition?.Symbol;
        Positions.Clear();
        foreach (var pos in (p.Positions ?? []).OrderByDescending(x => x.MarketValue ?? x.CostBasis))
            Positions.Add(new PositionRowViewModel(pos));
        HasDelayedPrices = Positions.Any(x => x.IsDelayed);
        SelectedPosition = Positions.FirstOrDefault(x => x.Symbol == selected);
    }

    partial void OnSelectedSourceFilterChanged(Option<CommandSource?>? value) => RefreshStats();

    [RelayCommand]
    private void RefreshStats()
    {
        if (services.Queries is not { } q) return;
        try
        {
            var tag = string.IsNullOrWhiteSpace(TagFilter) ? null : TagFilter.Trim();
            var s = q.GetPerformanceStats(SelectedSourceFilter?.Value, tag);
            Stats.Clear();
            Stats.Add(new("Trades", s.TradeCount.ToString()));
            Stats.Add(new("Gewinnquote", DisplayFormat.Percent(s.WinRate).TrimStart('+')));
            Stats.Add(new("Profit Factor", s.ProfitFactor.ToString("N2")));
            Stats.Add(new("Erwartungswert/Trade", DisplayFormat.SignedMoney(s.Expectancy), Math.Sign(s.Expectancy)));
            Stats.Add(new("Netto-GuV", DisplayFormat.SignedMoney(s.NetPnl), Math.Sign(s.NetPnl)));
            Stats.Add(new("Ø Gewinn", DisplayFormat.Money(s.AverageWin)));
            Stats.Add(new("Ø Verlust", DisplayFormat.Money(s.AverageLoss)));
            Stats.Add(new("Max. Drawdown", $"{DisplayFormat.Money(s.MaxDrawdown)} ({DisplayFormat.Percent(s.MaxDrawdownPercent).TrimStart('+')})"));
            Stats.Add(new("Gebühren", $"{DisplayFormat.Money(s.TotalFees)} ({DisplayFormat.Percent(s.FeeSharePercent).TrimStart('+')} Anteil)"));
            Stats.Add(new("Ø Haltedauer", FormatDuration(s.AverageHoldingTime)));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Kennzahlen konnten nicht berechnet werden");
            SetStatus("Kennzahlen nicht verfügbar: " + ex.GetBaseException().Message, true);
        }
    }

    [RelayCommand]
    private void OpenChart(PositionRowViewModel? row)
    {
        if (row is not null) selection.Select(row.Symbol, AppPage.Chart);
    }

    [RelayCommand]
    private void Trade(PositionRowViewModel? row)
    {
        if (row is not null) selection.Select(row.Symbol, AppPage.OrderTicket);
    }

    private static string FormatDuration(TimeSpan t) =>
        t == TimeSpan.Zero ? "–" :
        t.TotalDays >= 1 ? $"{t.TotalDays:0.#} Tage" :
        t.TotalHours >= 1 ? $"{t.TotalHours:0.#} Std" :
        $"{t.TotalMinutes:0} Min";
}
