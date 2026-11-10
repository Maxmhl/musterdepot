using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Musterdepot.Charting;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;

namespace Musterdepot.App.ViewModels;

/// <summary>Eine Zeile der Watchlist. Wird bei Kurs-Updates in place aktualisiert.</summary>
public sealed partial class QuoteRowViewModel(string symbol) : ObservableObject
{
    public string Symbol { get; } = symbol;

    [ObservableProperty] private string _last = "–";
    [ObservableProperty] private string _bid = "–";
    [ObservableProperty] private string _ask = "–";
    [ObservableProperty] private string _change = "–";
    [ObservableProperty] private int _changeSign;
    [ObservableProperty] private string _source = "–";
    [ObservableProperty] private string _asOf = "–";
    [ObservableProperty] private string _age = "";
    [ObservableProperty] private string? _delayBadge;
    [ObservableProperty] private bool _isDelayed;
    [ObservableProperty] private bool _hasQuote;
    [ObservableProperty] private string _toolTip = "Noch kein Kurs";

    private DateTimeOffset? _asOfUtc;

    public void Update(QuoteSnapshot? q, DateTimeOffset now)
    {
        if (q is null)
        {
            HasQuote = false;
            return;
        }
        HasQuote = true;
        Last = DisplayFormat.Price(q.Last ?? q.Mid);
        Bid = DisplayFormat.Price(q.Bid);
        Ask = DisplayFormat.Price(q.Ask);
        Change = DisplayFormat.Percent(q.ChangePercent);
        ChangeSign = q.ChangePercent is { } c ? Math.Sign(c) : 0;
        Source = q.Source;
        _asOfUtc = q.AsOfUtc;
        AsOf = q.AsOfUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);
        DelayBadge = DisplayFormat.DelayBadge(q);
        IsDelayed = DelayBadge is not null;
        ToolTip = IsDelayed
            ? $"Kurs von {q.Source}, Datenstand {q.AsOfUtc.ToLocalTime():dd.MM.yyyy HH:mm:ss}. {DelayBadge} – nicht für Echtzeit-Entscheidungen geeignet."
            : $"Echtzeitkurs von {q.Source}, Datenstand {q.AsOfUtc.ToLocalTime():dd.MM.yyyy HH:mm:ss}.";
        RefreshAge(now);
    }

    public void RefreshAge(DateTimeOffset now)
    {
        if (_asOfUtc is { } t) Age = DisplayFormat.Age(t, now);
    }
}

public sealed partial class PositionRowViewModel : ObservableObject
{
    public PositionRowViewModel(PositionSnapshot p)
    {
        Symbol = p.Symbol;
        Quantity = DisplayFormat.Quantity(p.Quantity);
        AverageCost = DisplayFormat.Price(p.AverageCost);
        CostBasis = DisplayFormat.Money(p.CostBasis);
        LastPrice = DisplayFormat.Price(p.LastPrice);
        MarketValue = DisplayFormat.Money(p.MarketValue);
        Unrealized = DisplayFormat.SignedMoney(p.UnrealizedPnl);
        UnrealizedPercent = DisplayFormat.Percent(p.UnrealizedPnlPercent);
        UnrealizedSign = p.UnrealizedPnl is { } u ? Math.Sign(u) : 0;
        Realized = DisplayFormat.SignedMoney(p.RealizedPnl);
        RealizedSign = Math.Sign(p.RealizedPnl);
        IsDelayed = p.PriceIsDelayed;
        PriceAsOf = p.PriceAsOfUtc?.ToLocalTime().ToString("dd.MM. HH:mm:ss", CultureInfo.CurrentCulture) ?? "–";
        Currency = p.QuoteCurrency;
        Lots = p.Lots.Count;
    }

    public string Symbol { get; }
    public string Quantity { get; }
    public string AverageCost { get; }
    public string CostBasis { get; }
    public string LastPrice { get; }
    public string MarketValue { get; }
    public string Unrealized { get; }
    public string UnrealizedPercent { get; }
    public int UnrealizedSign { get; }
    public string Realized { get; }
    public int RealizedSign { get; }
    public bool IsDelayed { get; }
    public string PriceAsOf { get; }
    public string Currency { get; }
    public int Lots { get; }
}

public sealed class OrderRowViewModel(Order o)
{
    public Order Order { get; } = o;
    public string OrderId => Order.OrderId;
    public string Created => Order.CreatedUtc.ToLocalTime().ToString("dd.MM. HH:mm:ss", CultureInfo.CurrentCulture);
    public string Symbol => Order.Symbol;
    public string Side => TradeCsv.Side(Order.Side);
    public int SideSign => Order.Side == OrderSide.Buy ? 1 : -1;
    public string Type => Labels.OrderType(Order.Type) + (Order.BracketRole != BracketRole.None ? $" ({Labels.Bracket(Order.BracketRole)})" : "");
    public string Quantity => DisplayFormat.Quantity(Order.Quantity);
    public string Filled => DisplayFormat.Quantity(Order.FilledQuantity);
    public string Limit => DisplayFormat.Price(Order.LimitPrice);
    public string Stop => DisplayFormat.Price(Order.StopPrice);
    public string AveragePrice => Order.FilledQuantity > 0 ? DisplayFormat.Price(Order.AveragePrice) : "–";
    public string Status => Labels.OrderStatus(Order.Status);
    public string Tif => Order.TimeInForce.ToString().ToUpperInvariant();
    public string Source => Labels.Source(Order.Source, Order.ClientName);
    public bool IsAi => Order.Source == CommandSource.Ai;
    public string Strategy => Order.StrategyTag ?? "";
    public string? RejectReason => Order.RejectReason;
}

public sealed class FillRowViewModel(Fill f)
{
    public Fill Fill { get; } = f;
    public string Time => Fill.TimestampUtc.ToLocalTime().ToString("dd.MM. HH:mm:ss", CultureInfo.CurrentCulture);
    public string Symbol => Fill.Symbol;
    public string Side => TradeCsv.Side(Fill.Side);
    public int SideSign => Fill.Side == OrderSide.Buy ? 1 : -1;
    public string Quantity => DisplayFormat.Quantity(Fill.Quantity);
    public string Price => DisplayFormat.Price(Fill.Price);
    public string Fee => DisplayFormat.Money(Fill.Fee);
    public string Realized => Fill.RealizedPnl == 0 ? "" : DisplayFormat.SignedMoney(Fill.RealizedPnl);
    public int RealizedSign => Math.Sign(Fill.RealizedPnl);
    public string Quality => TradeCsv.Quality(Fill.Quality);
    public bool IsSynthetic => Fill.Quality is FillQuality.Synthetic or FillQuality.Gap;
    public bool DuringGap => Fill.ExecutedDuringGap;
    public string Source => Fill.Source == CommandSource.Human ? "Mensch" : "KI";
    public bool IsAi => Fill.Source == CommandSource.Ai;
    public string Strategy => Fill.StrategyTag ?? "";
    public string Liquidity => Fill.Liquidity == Core.Domain.Liquidity.Maker ? "Maker" : "Taker";
}

public sealed class AuditRowViewModel(AuditEntry e)
{
    public AuditEntry Entry { get; } = e;
    public string Time => Entry.TimestampUtc.ToLocalTime().ToString("dd.MM. HH:mm:ss", CultureInfo.CurrentCulture);
    public string Client => Entry.ClientId;
    public string Source => Entry.Source == CommandSource.Human ? "Mensch" : "KI";
    public bool IsAi => Entry.Source == CommandSource.Ai;
    public string Action => Entry.Action;
    public string Result => Entry.Result;
    public bool IsFailure => Entry.Result is not ("Executed" or "Previewed" or "Duplicate" or "ok" or "Ok" or "success");
    public string Message => Entry.Message ?? "";
    public string ApprovedBy => Entry.ApprovedBy ?? "";
    public string Parameters => Entry.ParametersJson ?? "";
    public string Duration => Entry.Duration is { } d ? $"{d.TotalMilliseconds:0} ms" : "";
}

/// <summary>Eine offene Freigabeanfrage (KI-Aktivität und Always-on-top-Fenster).</summary>
public sealed partial class ApprovalItemViewModel : ObservableObject
{
    private readonly Func<ApprovalItemViewModel, bool, Task> _decide;

    public ApprovalItemViewModel(ApprovalRequest request, Func<ApprovalItemViewModel, bool, Task> decide)
    {
        Request = request;
        _decide = decide;
    }

    public ApprovalRequest Request { get; }
    public string ApprovalId => Request.ApprovalId;
    public string ClientName => Request.ClientName;
    public string Summary => Request.Summary;
    public string Created => Request.CreatedUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);
    public string CommandName => Request.Command.GetType().Name.Replace("Command", "");

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApproveCommand), nameof(DenyCommand))]
    private bool _isBusy;

    private bool CanDecide() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanDecide))]
    private Task Approve() => _decide(this, true);

    [RelayCommand(CanExecute = nameof(CanDecide))]
    private Task Deny() => _decide(this, false);
}

/// <summary>Deutsche Bezeichnungen für Enums.</summary>
public static class Labels
{
    public static string OrderType(OrderType t) => t switch
    {
        Core.Domain.OrderType.Market => "Market",
        Core.Domain.OrderType.Limit => "Limit",
        Core.Domain.OrderType.Stop => "Stop",
        Core.Domain.OrderType.StopLimit => "Stop-Limit",
        Core.Domain.OrderType.TrailingStop => "Trailing-Stop",
        _ => t.ToString(),
    };

    public static string Bracket(BracketRole r) => r switch
    {
        BracketRole.Entry => "Einstieg",
        BracketRole.TakeProfit => "Take-Profit",
        BracketRole.StopLoss => "Stop-Loss",
        _ => "",
    };

    public static string OrderStatus(OrderStatus s) => s switch
    {
        Core.Domain.OrderStatus.PendingNew => "neu",
        Core.Domain.OrderStatus.Working => "aktiv",
        Core.Domain.OrderStatus.Triggered => "ausgelöst",
        Core.Domain.OrderStatus.PartiallyFilled => "teilweise ausgeführt",
        Core.Domain.OrderStatus.Filled => "ausgeführt",
        Core.Domain.OrderStatus.Cancelled => "storniert",
        Core.Domain.OrderStatus.Rejected => "abgelehnt",
        Core.Domain.OrderStatus.Expired => "abgelaufen",
        _ => s.ToString(),
    };

    public static string Source(CommandSource s, string? client) =>
        s == CommandSource.Human ? "Mensch" : string.IsNullOrWhiteSpace(client) ? "KI" : $"KI ({client})";

    public static string Mode(ClientMode m) => m switch
    {
        ClientMode.ReadOnly => "read_only – nur lesen",
        ClientMode.Confirm => "confirm – jede Aktion bestätigen",
        ClientMode.Autonomous => "autonomous – innerhalb der Limits",
        _ => m.ToString(),
    };

    public static string Connection(ConnectionState s) => s switch
    {
        ConnectionState.Connected => "verbunden",
        ConnectionState.Connecting => "verbindet …",
        ConnectionState.Reconnecting => "verbindet neu …",
        ConnectionState.Suspended => "Standby",
        ConnectionState.Failed => "Fehler",
        _ => "getrennt",
    };
}
