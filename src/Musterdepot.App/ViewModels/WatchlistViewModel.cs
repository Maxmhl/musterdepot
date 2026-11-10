using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Musterdepot.App.Services;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Commands;
using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;

namespace Musterdepot.App.ViewModels;

public sealed record SearchResultItem(Instrument Instrument)
{
    public string Symbol => Instrument.Symbol;
    public string Name => Instrument.Name;
    public string Detail => $"{Instrument.AssetClass switch { AssetClass.Crypto => "Krypto", AssetClass.Stock => "Aktie", AssetClass.Etf => "ETF", _ => "FX" }} · {Instrument.QuoteCurrency}" +
                            (Instrument.PrimarySource is { } s ? $" · {s}" : "") +
                            (Instrument.AssetClass is AssetClass.Stock or AssetClass.Etf ? " · verzögert" : "");
    public bool IsDelayedClass => Instrument.AssetClass is AssetClass.Stock or AssetClass.Etf;
}

/// <summary>Watchlist: Live-Kurse, Änderung %, Quelle, Datenstand, Markierung "verzögert", Symbolsuche.</summary>
public sealed partial class WatchlistViewModel(
    AppServices services,
    IUiDispatcher ui,
    SelectionService selection,
    ILogger<WatchlistViewModel> logger) : PageViewModel
{
    private readonly List<IDisposable> _subscriptions = [];
    private IReadOnlyDictionary<string, QuoteSnapshot> _quotes = new Dictionary<string, QuoteSnapshot>();
    private CancellationTokenSource? _searchCts;
    private DispatcherTimer? _ageTimer;

    public override AppPage Page => AppPage.Watchlist;
    public override string Title => "Watchlist";
    public override string Glyph => "";

    public ObservableCollection<Watchlist> Watchlists { get; } = [];
    public ObservableCollection<QuoteRowViewModel> Rows { get; } = [];
    public ObservableCollection<SearchResultItem> SearchResults { get; } = [];

    public bool CanEdit => services.CommandBus is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteWatchlistCommand), nameof(AddSymbolCommand))]
    private Watchlist? _selectedWatchlist;

    [ObservableProperty] private QuoteRowViewModel? _selectedRow;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string _newWatchlistName = "";
    [ObservableProperty] private bool _showsAllQuotes;
    [ObservableProperty] private bool _hasDelayedRows;

    public override void Initialize()
    {
        if (services.ReadModels is { } rm)
        {
            _subscriptions.Add(rm.Watchlists.Subscribe(ui, ApplyWatchlists));
            // Kurse gedrosselt: höchstens 4 Aktualisierungen pro Sekunde.
            _subscriptions.Add(rm.Quotes.Subscribe(ui, q => { _quotes = q; RefreshRows(); }, TimeSpan.FromMilliseconds(250)));
        }
        else
        {
            SetStatus("Keine Kursdaten: Lesemodelle sind nicht verfügbar.", true);
        }

        _ageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _ageTimer.Tick += (_, _) =>
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var r in Rows) r.RefreshAge(now);
        };
        _ageTimer.Start();
    }

    private void ApplyWatchlists(IReadOnlyList<Watchlist> lists)
    {
        var selectedId = SelectedWatchlist?.WatchlistId;
        Watchlists.Clear();
        foreach (var w in lists) Watchlists.Add(w);
        SelectedWatchlist = Watchlists.FirstOrDefault(w => w.WatchlistId == selectedId) ?? Watchlists.FirstOrDefault();
        RefreshRows();
    }

    partial void OnSelectedWatchlistChanged(Watchlist? value) => RefreshRows();

    partial void OnSelectedRowChanged(QuoteRowViewModel? value)
    {
        if (value is not null) selection.SelectedSymbol = value.Symbol;
    }

    private void RefreshRows()
    {
        IEnumerable<string> symbols;
        if (SelectedWatchlist is { } w)
        {
            symbols = w.Symbols;
            ShowsAllQuotes = false;
        }
        else
        {
            // Ohne Watchlist: alle bekannten Kurse anzeigen.
            symbols = _quotes.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase);
            ShowsAllQuotes = true;
        }

        var wanted = symbols.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        for (int i = Rows.Count - 1; i >= 0; i--)
            if (!wanted.Contains(Rows[i].Symbol, StringComparer.OrdinalIgnoreCase)) Rows.RemoveAt(i);
        for (int i = 0; i < wanted.Count; i++)
        {
            var existing = Rows.FirstOrDefault(r => string.Equals(r.Symbol, wanted[i], StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                existing = new QuoteRowViewModel(wanted[i]);
                Rows.Insert(Math.Min(i, Rows.Count), existing);
            }
            else if (Rows.IndexOf(existing) != i)
            {
                Rows.Move(Rows.IndexOf(existing), Math.Min(i, Rows.Count - 1));
            }
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var r in Rows)
            r.Update(_quotes.TryGetValue(r.Symbol, out var q) ? q : services.Queries?.GetQuote(r.Symbol), now);
        HasDelayedRows = Rows.Any(r => r.IsDelayed);
    }

    partial void OnSearchTextChanged(string value) => _ = SearchAsync(value);

    private async Task SearchAsync(string text)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Length < 2)
        {
            SearchResults.Clear();
            return;
        }
        if (services.Queries is null)
        {
            SetStatus("Symbolsuche nicht verfügbar (Abfragen fehlen).", true);
            return;
        }
        try
        {
            await Task.Delay(300, cts.Token); // Entprellen beim Tippen
            IsSearching = true;
            var results = await services.Queries.SearchSymbolsAsync(text.Trim(), cts.Token);
            if (cts.IsCancellationRequested) return;
            SearchResults.Clear();
            foreach (var i in results.Take(30)) SearchResults.Add(new SearchResultItem(i));
            SetStatus(results.Count == 0 ? $"Nichts gefunden für „{text.Trim()}“." : null);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Symbolsuche fehlgeschlagen");
            SetStatus("Suche fehlgeschlagen: " + ex.GetBaseException().Message, true);
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts)) IsSearching = false;
        }
    }

    private bool HasWatchlist() => SelectedWatchlist is not null;

    [RelayCommand(CanExecute = nameof(HasWatchlist))]
    private async Task AddSymbol(SearchResultItem? item)
    {
        if (item is null || SelectedWatchlist is not { } w) return;
        var r = await services.SendAsync(new AddToWatchlistCommand { WatchlistId = w.WatchlistId, Symbol = item.Symbol });
        SetStatus(r.Success ? $"{item.Symbol} zu „{w.Name}“ hinzugefügt." : "Hinzufügen fehlgeschlagen: " + r.Message, !r.Success);
    }

    [RelayCommand]
    private async Task RemoveSymbol(QuoteRowViewModel? row)
    {
        if (row is null || SelectedWatchlist is not { } w) return;
        var r = await services.SendAsync(new RemoveFromWatchlistCommand { WatchlistId = w.WatchlistId, Symbol = row.Symbol });
        SetStatus(r.Success ? $"{row.Symbol} entfernt." : "Entfernen fehlgeschlagen: " + r.Message, !r.Success);
    }

    [RelayCommand]
    private async Task CreateWatchlist()
    {
        var name = string.IsNullOrWhiteSpace(NewWatchlistName) ? $"Watchlist {Watchlists.Count + 1}" : NewWatchlistName.Trim();
        var r = await services.SendAsync(new CreateWatchlistCommand { Name = name });
        if (r.Success)
        {
            NewWatchlistName = "";
            SetStatus($"Watchlist „{name}“ angelegt.");
            if (r.Data is Watchlist created) SelectedWatchlist = Watchlists.FirstOrDefault(x => x.WatchlistId == created.WatchlistId) ?? SelectedWatchlist;
        }
        else
        {
            SetStatus("Anlegen fehlgeschlagen: " + r.Message, true);
        }
    }

    [RelayCommand(CanExecute = nameof(HasWatchlist))]
    private async Task DeleteWatchlist()
    {
        if (SelectedWatchlist is not { } w) return;
        var r = await services.SendAsync(new DeleteWatchlistCommand { WatchlistId = w.WatchlistId });
        SetStatus(r.Success ? $"Watchlist „{w.Name}“ gelöscht." : "Löschen fehlgeschlagen: " + r.Message, !r.Success);
    }

    [RelayCommand]
    private void OpenChart(QuoteRowViewModel? row)
    {
        if (row is not null) selection.Select(row.Symbol, AppPage.Chart);
    }

    [RelayCommand]
    private void Trade(QuoteRowViewModel? row)
    {
        if (row is not null) selection.Select(row.Symbol, AppPage.OrderTicket);
    }

    [RelayCommand]
    private void OpenSearchResultChart(SearchResultItem? item)
    {
        if (item is not null) selection.Select(item.Symbol, AppPage.Chart);
    }
}
