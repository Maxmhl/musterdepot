using System.Collections.Concurrent;
using System.ComponentModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Musterdepot.App.Services;
using Musterdepot.Charting;
using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;

namespace Musterdepot.App.ViewModels;

public sealed record TimeframeOption(Timeframe Value, string Label);

/// <summary>
/// Chart in WebView2 (Lightweight Charts). Kerzen, Indikatoren, Preislinien und Lücken werden hier berechnet und als
/// fertige Werte per <see cref="MessageReady"/> an die View gegeben, die sie mit PostWebMessageAsJson weiterreicht.
/// </summary>
public sealed partial class ChartViewModel : PageViewModel
{
    private readonly AppServices _services;
    private readonly IUiDispatcher _ui;
    private readonly SelectionService _selection;
    private readonly ThemeService _theme;
    private readonly ILogger<ChartViewModel> _logger;
    private readonly ConcurrentQueue<MarketEvent> _incoming = new();
    private readonly Dictionary<string, List<ChartGap>> _gapsBySymbol = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IDisposable> _subscriptions = [];
    private LiveCandleSeries? _series;
    private IReadOnlyList<IndicatorSeries> _indicators = [];
    private DispatcherTimer? _pump;
    private CancellationTokenSource? _loadCts;
    private bool _viewReady;
    private IReadOnlyList<ChartPriceLine> _lastLines = [];

    public ChartViewModel(AppServices services, IUiDispatcher ui, SelectionService selection, ThemeService theme, ILogger<ChartViewModel> logger)
    {
        _services = services;
        _ui = ui;
        _selection = selection;
        _theme = theme;
        _logger = logger;
        _selectedTimeframe = Timeframes[0];
        selection.PropertyChanged += OnSelectionChanged;
        theme.ThemeChanged += (_, dark) => Send(new ThemeMessage(dark));
    }

    public override AppPage Page => AppPage.Chart;
    public override string Title => "Chart";
    public override string Glyph => "";

    /// <summary>Nachricht an chart.js (UI-Thread).</summary>
    public event EventHandler<ChartMessage>? MessageReady;

    public IReadOnlyList<TimeframeOption> Timeframes { get; } =
    [
        new(Timeframe.M1, "1 Min"), new(Timeframe.M5, "5 Min"), new(Timeframe.M15, "15 Min"), new(Timeframe.M30, "30 Min"),
        new(Timeframe.H1, "1 Std"), new(Timeframe.H4, "4 Std"), new(Timeframe.D1, "1 Tag"), new(Timeframe.W1, "1 Woche"),
    ];

    [ObservableProperty] private string? _symbol;
    [ObservableProperty] private TimeframeOption _selectedTimeframe;
    [ObservableProperty] private bool _showEmaFast = true;
    [ObservableProperty] private bool _showEmaSlow;
    [ObservableProperty] private bool _showVwap;
    [ObservableProperty] private bool _showRsi = true;
    [ObservableProperty] private bool _showBollinger;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _delayNotice;
    [ObservableProperty] private string? _lastPrice;
    [ObservableProperty] private string? _webViewError;

    public override void Initialize()
    {
        Symbol = _selection.SelectedSymbol;
        if (_services.ReadModels is { } rm)
        {
            rm.MarketEventReceived += OnMarketEvent;
            _subscriptions.Add(rm.OpenOrders.Subscribe(_ui, _ => UpdatePriceLines(), TimeSpan.FromMilliseconds(300)));
            _subscriptions.Add(rm.Portfolio.Subscribe(_ui, _ => UpdatePriceLines(), TimeSpan.FromMilliseconds(1000)));
        }
        // Live-Ticks werden gesammelt und 4× pro Sekunde auf dem UI-Thread angewendet (gedrosselt).
        _pump = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _pump.Tick += (_, _) => Pump();
        _pump.Start();
    }

    private void OnSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SelectionService.SelectedSymbol) && _selection.SelectedSymbol is { } s &&
            !string.Equals(s, Symbol, StringComparison.OrdinalIgnoreCase))
            Symbol = s;
    }

    partial void OnSymbolChanged(string? value) => _ = LoadAsync();
    partial void OnSelectedTimeframeChanged(TimeframeOption value) => _ = LoadAsync();
    partial void OnShowEmaFastChanged(bool value) => RecomputeIndicators();
    partial void OnShowEmaSlowChanged(bool value) => RecomputeIndicators();
    partial void OnShowVwapChanged(bool value) => RecomputeIndicators();
    partial void OnShowRsiChanged(bool value) => RecomputeIndicators();
    partial void OnShowBollingerChanged(bool value) => RecomputeIndicators();

    protected override void OnActivated()
    {
        if (_series is null && Symbol is not null) _ = LoadAsync();
    }

    /// <summary>chart.js hat "ready" gemeldet (auch nach Neuladen der Seite).</summary>
    public void OnViewReady()
    {
        _viewReady = true;
        Send(new ThemeMessage(_theme.IsDark));
        if (_series is not null) SendLoad();
        else if (Symbol is not null) _ = LoadAsync();
        else Send(new StatusMessage("Symbol in der Watchlist wählen"));
    }

    public void OnViewError(string message)
    {
        WebViewError = message;
        _logger.LogWarning("Chart-Fehler: {Message}", message);
    }

    private IndicatorOptions Options => new()
    {
        EmaFast = ShowEmaFast,
        EmaSlow = ShowEmaSlow,
        Vwap = ShowVwap,
        Rsi = ShowRsi,
        Bollinger = ShowBollinger,
    };

    [RelayCommand]
    private Task Reload() => LoadAsync();

    [RelayCommand]
    private void Trade()
    {
        if (Symbol is not null) _selection.Select(Symbol, AppPage.OrderTicket);
    }

    private async Task LoadAsync()
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        var symbol = Symbol;
        var tf = SelectedTimeframe.Value;
        _series = null;
        if (symbol is null)
        {
            Send(new StatusMessage("Symbol in der Watchlist wählen"));
            return;
        }
        if (_services.Queries is null)
        {
            SetStatus("Kerzen nicht verfügbar: Abfragen (ITradingQueries) fehlen.", true);
            Send(new StatusMessage("Keine Daten – Abfragen nicht verfügbar"));
            return;
        }

        IsLoading = true;
        Send(new StatusMessage($"Lade {symbol} …"));
        try
        {
            var candles = await _services.Queries.GetCandlesAsync(symbol, tf, null, null, 500, cts.Token);
            if (cts.IsCancellationRequested) return;
            _series = new LiveCandleSeries(symbol, tf, candles);
            while (_incoming.TryDequeue(out _)) { } // alte Ticks verwerfen
            var quote = _services.ReadModels?.Quotes.Current?.GetValueOrDefault(symbol) ?? _services.Queries.GetQuote(symbol);
            DelayNotice = DisplayFormat.DelayBadge(quote) is { } badge
                ? $"{badge} (Quelle {quote!.Source}, Stand {quote.AsOfUtc.ToLocalTime():HH:mm}). Aktienkurse sind 15–30 Min alt – der Chart zeigt nicht den aktuellen Markt."
                : null;
            LastPrice = DisplayFormat.Price(quote?.Last ?? _series.Candles.LastOrDefault()?.Close);
            RecomputeIndicators(send: false);
            SendLoad();
            SetStatus(candles.Count == 0 ? "Noch keine Kerzen für diesen Zeitrahmen." : null);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Kerzen für {Symbol} konnten nicht geladen werden", symbol);
            SetStatus("Kerzen konnten nicht geladen werden: " + ex.GetBaseException().Message, true);
            Send(new StatusMessage("Fehler beim Laden"));
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts)) IsLoading = false;
        }
    }

    private void SendLoad()
    {
        if (_series is null) return;
        var candles = _series.Candles;
        Send(new LoadMessage
        {
            Symbol = _series.Symbol,
            Timeframe = SelectedTimeframe.Label,
            PriceDecimals = ChartMapper.SuggestPriceDecimals(candles),
            Bars = ChartMapper.ToBars(candles),
            Indicators = _indicators,
            Gaps = CurrentGaps(),
            PriceLines = _lastLines = BuildPriceLines(),
            Notice = DelayNotice is null ? null : "verzögert",
        });
    }

    private void RecomputeIndicators() => RecomputeIndicators(send: true);

    private void RecomputeIndicators(bool send)
    {
        if (_series is null) return;
        _indicators = ChartMapper.ComputeIndicators(_series.Candles, Options);
        if (send) Send(new IndicatorsMessage(_indicators));
    }

    // Hintergrund-Thread: nur einsammeln.
    private void OnMarketEvent(object? sender, MarketEvent e)
    {
        var s = Symbol;
        if (s is null || !string.Equals(e.Symbol, s, StringComparison.OrdinalIgnoreCase)) return;
        if (e is TradeTick or QuoteTick or CandleUpdate or GapDetected) _incoming.Enqueue(e);
    }

    private void Pump()
    {
        if (_series is null) { while (_incoming.TryDequeue(out _)) { } return; }
        Candle? changed = null;
        var reload = false;
        var budget = 5000;
        while (budget-- > 0 && _incoming.TryDequeue(out var e))
        {
            if (e is GapDetected gap)
            {
                AddGap(gap);
                _series.Merge(gap.BackfilledCandles);
                reload = true;
                continue;
            }
            changed = _series.Apply(e) ?? changed;
            switch (e)
            {
                case TradeTick t: LastPrice = DisplayFormat.Price(t.Price); break;
                case QuoteTick { Last: { } last }: LastPrice = DisplayFormat.Price(last); break;
            }
        }

        if (reload)
        {
            RecomputeIndicators(send: false);
            SendLoad();
            return;
        }
        if (changed is null) return;
        RecomputeIndicators(send: false);
        var bar = ChartMapper.ToBar(_series.Candles[^1]);
        Send(new BarMessage(bar, ChartMapper.LastPoints(_indicators)));
    }

    private void AddGap(GapDetected gap)
    {
        if (!_gapsBySymbol.TryGetValue(gap.Symbol, out var list)) _gapsBySymbol[gap.Symbol] = list = [];
        list.Add(ChartMapper.ToGap(gap, SelectedTimeframe.Value));
        SetStatus($"Datenlücke erkannt und nachgeladen: {list[^1].Label}");
    }

    private IReadOnlyList<ChartGap> CurrentGaps()
    {
        if (Symbol is null || !_gapsBySymbol.TryGetValue(Symbol, out var list)) return [];
        var tf = SelectedTimeframe.Value;
        // Lücken auf den aktuellen Zeitrahmen umrechnen (gespeichert in der Auflösung beim Eintreffen).
        return list.Select(g => g with
        {
            Start = tf.Floor(DateTimeOffset.FromUnixTimeSeconds(g.Start)).ToUnixTimeSeconds(),
            End = tf.Floor(DateTimeOffset.FromUnixTimeSeconds(g.End)).ToUnixTimeSeconds(),
        }).ToList();
    }

    private void UpdatePriceLines()
    {
        if (_series is null) return;
        var lines = BuildPriceLines();
        if (lines.SequenceEqual(_lastLines)) return;
        _lastLines = lines;
        Send(new PriceLinesMessage(lines));
    }

    /// <summary>Eigene offene Orders (Limit/Stop) und Position (Einstand) als Preislinien.</summary>
    private IReadOnlyList<ChartPriceLine> BuildPriceLines()
    {
        var symbol = Symbol;
        var rm = _services.ReadModels;
        if (symbol is null || rm is null) return [];
        var lines = new List<ChartPriceLine>();
        foreach (var o in (rm.OpenOrders.Current ?? []).Where(o => string.Equals(o.Symbol, symbol, StringComparison.OrdinalIgnoreCase)))
        {
            var color = o.Side == OrderSide.Buy ? "#26A69A" : "#EF5350";
            var who = o.Source == CommandSource.Ai ? " · KI" : "";
            var side = o.Side == OrderSide.Buy ? "Kauf" : "Verkauf";
            var qty = DisplayFormat.Quantity(o.RemainingQuantity);
            if (o.LimitPrice is { } lp)
                lines.Add(new ChartPriceLine { Id = o.OrderId + ":l", Price = lp, Color = color, Title = $"{side} Limit {qty}{who}", LineStyle = 2 });
            if (o.StopPrice is { } sp)
                lines.Add(new ChartPriceLine { Id = o.OrderId + ":s", Price = sp, Color = "#FF9800", Title = $"{side} Stop {qty}{who}", LineStyle = 1 });
        }
        var pos = rm.Portfolio.Current?.Positions?.FirstOrDefault(p => string.Equals(p.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
        if (pos is { Quantity: not 0 })
            lines.Add(new ChartPriceLine
            {
                Id = "position",
                Price = pos.AverageCost,
                Color = "#2962FF",
                Title = $"Position {DisplayFormat.Quantity(pos.Quantity)} · Einstand",
                LineStyle = 0,
                LineWidth = 2,
            });
        return lines;
    }

    private void Send(ChartMessage message)
    {
        if (!_viewReady) return;
        MessageReady?.Invoke(this, message);
    }

    /// <summary>Die View wurde entladen (WebView2 neu erstellt) – bis zum nächsten "ready" nichts senden.</summary>
    public void OnViewDetached() => _viewReady = false;
}
