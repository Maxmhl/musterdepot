using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Musterdepot.App.Services;
using Musterdepot.Charting;
using Musterdepot.Core.Commands;
using Musterdepot.Core.Domain;

namespace Musterdepot.App.ViewModels;

public sealed record Option<T>(T Value, string Label);

/// <summary>
/// Orderticket: Kauf/Verkauf, Market/Limit/Stop/Stop-Limit (+ Trailing, Bracket als „erweitert“), Stückzahl oder Betrag,
/// Gültigkeit, Strategie-Tag. Gebührenvorschau live beim Tippen über PlaceOrderCommand{DryRun=true}.
/// </summary>
public sealed partial class OrderTicketViewModel : PageViewModel
{
    private readonly AppServices _services;
    private readonly SelectionService _selection;
    private readonly IDialogService _dialogs;
    private readonly ILogger<OrderTicketViewModel> _logger;
    private CancellationTokenSource? _previewCts;
    /// <summary>Ein Schlüssel je Entwurf: Doppelklick auf „Senden“ erzeugt keine Doppelorder.</summary>
    private string _idempotencyKey = NewKey();

    public OrderTicketViewModel(AppServices services, SelectionService selection, IDialogService dialogs, ILogger<OrderTicketViewModel> logger)
    {
        _services = services;
        _selection = selection;
        _dialogs = dialogs;
        _logger = logger;
        _selectedType = OrderTypes[0];
        _selectedTif = TimeInForces[0];
        selection.PropertyChanged += OnSelectionChanged;
        PropertyChanged += OnAnyPropertyChanged;
    }

    public override AppPage Page => AppPage.OrderTicket;
    public override string Title => "Orderticket";
    public override string Glyph => "";

    public IReadOnlyList<Option<OrderType>> OrderTypes { get; } =
    [
        new(OrderType.Market, "Market"), new(OrderType.Limit, "Limit"), new(OrderType.Stop, "Stop"),
        new(OrderType.StopLimit, "Stop-Limit"), new(OrderType.TrailingStop, "Trailing-Stop (erweitert)"),
    ];

    public IReadOnlyList<Option<TimeInForce>> TimeInForces { get; } =
    [
        new(TimeInForce.Day, "DAY – bis Handelsschluss"), new(TimeInForce.Gtc, "GTC – bis auf Widerruf"),
        new(TimeInForce.Ioc, "IOC – sofort oder verwerfen"),
    ];

    public ObservableCollection<string> PreviewWarnings { get; } = [];

    [ObservableProperty] private string _symbol = "";
    [ObservableProperty] private bool _isBuy = true;
    [ObservableProperty] private Option<OrderType> _selectedType;
    [ObservableProperty] private bool _useNotional;
    [ObservableProperty] private string _quantityText = "";
    [ObservableProperty] private string _notionalText = "";
    [ObservableProperty] private string _limitText = "";
    [ObservableProperty] private string _stopText = "";
    [ObservableProperty] private string _trailAmountText = "";
    [ObservableProperty] private string _trailPercentText = "";
    [ObservableProperty] private Option<TimeInForce> _selectedTif;
    [ObservableProperty] private string _strategyTag = "";
    [ObservableProperty] private bool _showAdvanced;
    [ObservableProperty] private bool _useBracket;
    [ObservableProperty] private string _takeProfitText = "";
    [ObservableProperty] private string _stopLossText = "";

    [ObservableProperty] private bool _isPreviewing;
    [ObservableProperty] private bool _previewOk;
    [ObservableProperty] private string? _previewError;
    [ObservableProperty] private string _previewQuantity = "–";
    [ObservableProperty] private string _previewPrice = "–";
    [ObservableProperty] private string _previewNotional = "–";
    [ObservableProperty] private string _previewFee = "–";
    [ObservableProperty] private string _previewCashAfter = "–";
    [ObservableProperty] private bool _previewDelayed;
    [ObservableProperty] private string _quoteInfo = "";
    [ObservableProperty] private string? _quoteDelayBadge;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private bool _isSubmitting;

    public bool NeedsLimit => SelectedType.Value is OrderType.Limit or OrderType.StopLimit;
    public bool NeedsStop => SelectedType.Value is OrderType.Stop or OrderType.StopLimit;
    public bool IsTrailing => SelectedType.Value == OrderType.TrailingStop;
    public string SubmitLabel => IsBuy ? "Kauforder senden" : "Verkaufsorder senden";

    private void OnSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SelectionService.SelectedSymbol) && _selection.SelectedSymbol is { } s) Symbol = s;
    }

    public override void Initialize()
    {
        if (_selection.SelectedSymbol is { } s) Symbol = s;
    }

    protected override void OnActivated() => SchedulePreview();

    partial void OnSelectedTypeChanged(Option<OrderType> value)
    {
        OnPropertyChanged(nameof(NeedsLimit));
        OnPropertyChanged(nameof(NeedsStop));
        OnPropertyChanged(nameof(IsTrailing));
        if (value.Value == OrderType.TrailingStop) ShowAdvanced = true;
    }

    partial void OnIsBuyChanged(bool value) => OnPropertyChanged(nameof(SubmitLabel));

    partial void OnSymbolChanged(string value) => UpdateQuoteInfo();

    private static readonly HashSet<string> PreviewInputs =
    [
        nameof(Symbol), nameof(IsBuy), nameof(SelectedType), nameof(UseNotional), nameof(QuantityText), nameof(NotionalText),
        nameof(LimitText), nameof(StopText), nameof(TrailAmountText), nameof(TrailPercentText), nameof(SelectedTif),
        nameof(UseBracket), nameof(TakeProfitText), nameof(StopLossText),
    ];

    private void OnAnyPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is { } n && PreviewInputs.Contains(n))
        {
            _idempotencyKey = NewKey(); // geänderter Entwurf = neue Order
            SchedulePreview();
        }
    }

    private void UpdateQuoteInfo()
    {
        var q = string.IsNullOrWhiteSpace(Symbol) ? null : _services.Queries?.GetQuote(Symbol.Trim());
        if (q is null)
        {
            QuoteInfo = string.IsNullOrWhiteSpace(Symbol) ? "" : "Kein aktueller Kurs";
            QuoteDelayBadge = null;
            return;
        }
        QuoteInfo = $"Geld {DisplayFormat.Price(q.Bid)} · Brief {DisplayFormat.Price(q.Ask)} · Letzter {DisplayFormat.Price(q.Last)} · {q.Source}, {q.AsOfUtc.ToLocalTime():HH:mm:ss}";
        QuoteDelayBadge = DisplayFormat.DelayBadge(q);
    }

    /// <summary>Baut den Befehl aus den Eingaben; Fehlertext bei ungültigen Eingaben.</summary>
    private PlaceOrderCommand? BuildCommand(bool dryRun, out string? error)
    {
        error = null;
        var symbol = Symbol.Trim();
        if (symbol.Length == 0) { error = "Symbol fehlt."; return null; }

        decimal? Parse(string text, string label, ref string? err)
        {
            if (NumberInput.TryParse(text, out var v)) return v;
            err ??= $"{label}: keine gültige Zahl.";
            return null;
        }

        var qty = UseNotional ? null : Parse(QuantityText, "Stückzahl", ref error);
        var notional = UseNotional ? Parse(NotionalText, "Betrag", ref error) : null;
        var limit = NeedsLimit ? Parse(LimitText, "Limit", ref error) : null;
        var stop = NeedsStop ? Parse(StopText, "Stop", ref error) : null;
        var trailAmount = IsTrailing ? Parse(TrailAmountText, "Trailing-Abstand", ref error) : null;
        var trailPercent = IsTrailing ? Parse(TrailPercentText, "Trailing-Prozent", ref error) : null;
        var tp = UseBracket ? Parse(TakeProfitText, "Take-Profit", ref error) : null;
        var sl = UseBracket ? Parse(StopLossText, "Stop-Loss", ref error) : null;
        if (error is not null) return null;

        if (UseNotional ? notional is not > 0 : qty is not > 0) { error = UseNotional ? "Betrag eingeben." : "Stückzahl eingeben."; return null; }
        if (NeedsLimit && limit is not > 0) { error = "Limitpreis eingeben."; return null; }
        if (NeedsStop && stop is not > 0) { error = "Stoppreis eingeben."; return null; }
        if (IsTrailing && trailAmount is null && trailPercent is null) { error = "Trailing-Abstand (Betrag oder %) eingeben."; return null; }
        if (UseBracket && tp is null && sl is null) { error = "Für Bracket Take-Profit und/oder Stop-Loss eingeben."; return null; }

        return new PlaceOrderCommand
        {
            Symbol = symbol,
            Side = IsBuy ? OrderSide.Buy : OrderSide.Sell,
            Type = SelectedType.Value,
            Quantity = qty,
            Notional = notional,
            LimitPrice = limit,
            StopPrice = stop,
            TrailAmount = trailAmount,
            TrailPercent = trailPercent,
            TimeInForce = SelectedTif.Value,
            StrategyTag = string.IsNullOrWhiteSpace(StrategyTag) ? null : StrategyTag.Trim(),
            TakeProfitPrice = tp,
            StopLossPrice = sl,
            DryRun = dryRun,
            Source = CommandSource.Human,
            ClientId = "ui",
            IdempotencyKey = dryRun ? NewKey() : _idempotencyKey,
        };
    }

    private void SchedulePreview() => _ = PreviewAsync();

    private async Task PreviewAsync()
    {
        _previewCts?.Cancel();
        var cts = _previewCts = new CancellationTokenSource();
        UpdateQuoteInfo();
        var cmd = BuildCommand(dryRun: true, out var error);
        if (cmd is null)
        {
            ResetPreview(error);
            return;
        }
        try
        {
            await Task.Delay(250, cts.Token); // Entprellen beim Tippen
            IsPreviewing = true;
            var result = await _services.SendAsync(cmd, cts.Token);
            if (cts.IsCancellationRequested) return;
            ApplyPreview(result);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ordervorschau fehlgeschlagen");
            ResetPreview("Vorschau fehlgeschlagen: " + ex.GetBaseException().Message);
        }
        finally
        {
            if (ReferenceEquals(_previewCts, cts)) IsPreviewing = false;
        }
    }

    private void ApplyPreview(CommandResult result)
    {
        PreviewWarnings.Clear();
        if (result.Data is OrderPreview p)
        {
            PreviewOk = p.WouldBeAccepted;
            PreviewError = p.WouldBeAccepted ? null : $"Würde abgelehnt: {p.RejectReason}{(p.RiskRule is { } r ? $" (Regel {r})" : "")}";
            PreviewQuantity = DisplayFormat.Quantity(p.Quantity);
            PreviewPrice = DisplayFormat.Price(p.EstimatedPrice);
            PreviewNotional = DisplayFormat.Money(p.EstimatedNotional);
            PreviewFee = DisplayFormat.Money(p.EstimatedFee);
            PreviewCashAfter = DisplayFormat.Money(p.EstimatedCashAfter);
            PreviewDelayed = p.PriceIsDelayed;
            foreach (var w in p.Warnings) PreviewWarnings.Add(w);
            if (p.PriceIsDelayed)
                PreviewWarnings.Add("Schätzung beruht auf verzögerten Kursen – die Ausführung erfolgt erst gegen Kurse nach Ordereingang (Fill-Qualität SYNTHETIC).");
        }
        else
        {
            ResetPreview(result.Success ? "Keine Vorschau erhalten." : result.Message ?? result.Status.ToString());
        }
    }

    private void ResetPreview(string? error)
    {
        PreviewOk = false;
        PreviewError = error;
        PreviewQuantity = PreviewPrice = PreviewNotional = PreviewFee = PreviewCashAfter = "–";
        PreviewDelayed = false;
        PreviewWarnings.Clear();
    }

    private bool CanSubmit() => !IsSubmitting;

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task Submit()
    {
        var cmd = BuildCommand(dryRun: false, out var error);
        if (cmd is null)
        {
            SetStatus(error, true);
            return;
        }
        if (PreviewDelayed && !_dialogs.Confirm("Verzögerte Kurse",
                "Für dieses Instrument liegen nur verzögerte Kurse vor (15–30 Min).\n\nDie Order wird erst gegen Kurse nach dem Ordereingang simuliert. Trotzdem senden?"))
            return;

        IsSubmitting = true;
        try
        {
            var r = await _services.SendAsync(cmd);
            switch (r.Status)
            {
                case CommandStatus.Executed:
                case CommandStatus.Duplicate:
                    var id = r.Data is Order o ? o.OrderId : null;
                    SetStatus($"Order gesendet{(id is null ? "" : $" ({id})")}{(r.Status == CommandStatus.Duplicate ? " – war bereits gesendet" : "")}.");
                    _idempotencyKey = NewKey();
                    break;
                case CommandStatus.PendingApproval:
                    SetStatus("Order wartet auf Freigabe.");
                    break;
                default:
                    SetStatus($"Order abgelehnt: {r.Message ?? r.Status.ToString()}", true);
                    break;
            }
        }
        finally
        {
            IsSubmitting = false;
            SchedulePreview();
        }
    }

    [RelayCommand]
    private void SetSide(string side) => IsBuy = side == "buy";

    [RelayCommand]
    private void Clear()
    {
        QuantityText = NotionalText = LimitText = StopText = TrailAmountText = TrailPercentText = TakeProfitText = StopLossText = "";
        UseBracket = false;
        SetStatus(null);
    }

    private static string NewKey() => "ui_" + Guid.NewGuid().ToString("N");
}
