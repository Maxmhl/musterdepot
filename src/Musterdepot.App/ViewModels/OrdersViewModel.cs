using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Musterdepot.App.Services;
using Musterdepot.Charting;
using Musterdepot.Core.Commands;
using Musterdepot.Core.Domain;

namespace Musterdepot.App.ViewModels;

/// <summary>Orders und Historie: offene Orders (stornieren/ändern), Ausführungen, Order-Historie, CSV-Export.</summary>
public sealed partial class OrdersViewModel(
    AppServices services,
    IUiDispatcher ui,
    IDialogService dialogs,
    ILogger<OrdersViewModel> logger) : PageViewModel
{
    private readonly List<IDisposable> _subscriptions = [];
    private IReadOnlyList<Fill> _fills = [];
    private IReadOnlyList<Order> _allOrders = [];

    public override AppPage Page => AppPage.Orders;
    public override string Title => "Orders & Historie";
    public override string Glyph => "";

    public ObservableCollection<OrderRowViewModel> OpenOrders { get; } = [];
    public ObservableCollection<OrderRowViewModel> OrderHistory { get; } = [];
    public ObservableCollection<FillRowViewModel> Fills { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ModifyCommand))]
    private OrderRowViewModel? _selectedOpenOrder;

    [ObservableProperty] private string _modifyQuantity = "";
    [ObservableProperty] private string _modifyLimit = "";
    [ObservableProperty] private string _modifyStop = "";
    [ObservableProperty] private string _filterText = "";

    public override void Initialize()
    {
        if (services.ReadModels is not { } rm)
        {
            SetStatus("Orders nicht verfügbar: Lesemodelle fehlen.", true);
            return;
        }
        _subscriptions.Add(rm.OpenOrders.Subscribe(ui, ApplyOpenOrders, TimeSpan.FromMilliseconds(200)));
        _subscriptions.Add(rm.AllOrders.Subscribe(ui, o => { _allOrders = o ?? []; ApplyHistory(); }, TimeSpan.FromMilliseconds(500)));
        _subscriptions.Add(rm.Fills.Subscribe(ui, f => { _fills = f ?? []; ApplyFills(); }, TimeSpan.FromMilliseconds(500)));
    }

    private void ApplyOpenOrders(IReadOnlyList<Order>? orders)
    {
        var selectedId = SelectedOpenOrder?.OrderId;
        OpenOrders.Clear();
        foreach (var o in (orders ?? []).OrderByDescending(o => o.CreatedUtc)) OpenOrders.Add(new OrderRowViewModel(o));
        SelectedOpenOrder = OpenOrders.FirstOrDefault(o => o.OrderId == selectedId);
    }

    partial void OnFilterTextChanged(string value)
    {
        ApplyHistory();
        ApplyFills();
    }

    private bool Matches(string symbol, string? tag) =>
        string.IsNullOrWhiteSpace(FilterText) ||
        symbol.Contains(FilterText.Trim(), StringComparison.OrdinalIgnoreCase) ||
        (tag?.Contains(FilterText.Trim(), StringComparison.OrdinalIgnoreCase) ?? false);

    private void ApplyHistory()
    {
        OrderHistory.Clear();
        foreach (var o in _allOrders.Where(o => Matches(o.Symbol, o.StrategyTag)).OrderByDescending(o => o.CreatedUtc).Take(1000))
            OrderHistory.Add(new OrderRowViewModel(o));
    }

    private void ApplyFills()
    {
        Fills.Clear();
        foreach (var f in _fills.Where(f => Matches(f.Symbol, f.StrategyTag)).OrderByDescending(f => f.TimestampUtc).Take(1000))
            Fills.Add(new FillRowViewModel(f));
    }

    partial void OnSelectedOpenOrderChanged(OrderRowViewModel? value)
    {
        ModifyQuantity = value is null ? "" : NumberInput.Format(value.Order.Quantity);
        ModifyLimit = value is null ? "" : NumberInput.Format(value.Order.LimitPrice);
        ModifyStop = value is null ? "" : NumberInput.Format(value.Order.StopPrice);
    }

    [RelayCommand]
    private async Task Cancel(OrderRowViewModel? row)
    {
        if (row is null) return;
        var r = await services.SendAsync(new CancelOrderCommand { OrderId = row.OrderId, Source = CommandSource.Human });
        SetStatus(r.Success ? $"Order {row.OrderId} storniert." : "Stornieren fehlgeschlagen: " + r.Message, !r.Success);
    }

    [RelayCommand]
    private async Task CancelAll()
    {
        if (OpenOrders.Count == 0) return;
        if (!dialogs.Confirm("Alle stornieren", $"Alle {OpenOrders.Count} offenen Orders stornieren?", warning: true)) return;
        var r = await services.SendAsync(new CancelAllOrdersCommand { Source = CommandSource.Human });
        SetStatus(r.Success ? "Alle offenen Orders storniert." : "Stornieren fehlgeschlagen: " + r.Message, !r.Success);
    }

    private bool CanModify() => SelectedOpenOrder is not null;

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task Modify()
    {
        if (SelectedOpenOrder is not { } row) return;
        if (!NumberInput.TryParse(ModifyQuantity, out var qty) || !NumberInput.TryParse(ModifyLimit, out var limit) ||
            !NumberInput.TryParse(ModifyStop, out var stop))
        {
            SetStatus("Bitte gültige Zahlen eingeben.", true);
            return;
        }
        var o = row.Order;
        var cmd = new ModifyOrderCommand
        {
            OrderId = o.OrderId,
            Source = CommandSource.Human,
            NewQuantity = qty != o.Quantity ? qty : null,
            NewLimitPrice = limit != o.LimitPrice ? limit : null,
            NewStopPrice = stop != o.StopPrice ? stop : null,
        };
        if (cmd.NewQuantity is null && cmd.NewLimitPrice is null && cmd.NewStopPrice is null)
        {
            SetStatus("Keine Änderung.");
            return;
        }
        var r = await services.SendAsync(cmd);
        SetStatus(r.Success ? $"Order {o.OrderId} geändert." : "Ändern fehlgeschlagen: " + r.Message, !r.Success);
    }

    /// <summary>Parameter "de" = Semikolon/Komma für deutsches Excel, sonst international.</summary>
    [RelayCommand]
    private void ExportFills(string? format) =>
        Export("Ausfuehrungen", format, f => TradeCsv.Fills(services.Queries?.GetTradeHistory(null, null) ?? _fills, f));

    [RelayCommand]
    private void ExportOrders(string? format) =>
        Export("Orders", format, f => TradeCsv.Orders(_allOrders.Count > 0 ? _allOrders : services.Queries?.ListOrders(false) ?? [], f));

    private void Export(string name, string? format, Func<CsvFormat, string> build)
    {
        try
        {
            var german = string.Equals(format, "de", StringComparison.OrdinalIgnoreCase);
            var csvFormat = german ? CsvFormat.GermanExcel() : CsvFormat.Invariant;
            var content = build(csvFormat);
            var suffix = german ? "excel-de" : "invariant";
            var path = dialogs.SaveText($"Musterdepot-{name}-{DateTime.Now:yyyyMMdd-HHmm}-{suffix}.csv",
                "CSV-Datei (*.csv)|*.csv|Alle Dateien (*.*)|*.*", content, csvFormat.Encoding);
            if (path is not null) SetStatus($"Exportiert: {path}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "CSV-Export fehlgeschlagen");
            SetStatus("Export fehlgeschlagen: " + ex.GetBaseException().Message, true);
        }
    }
}
