using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Musterdepot.App.Hosting;
using Musterdepot.App.Services;
using Musterdepot.Charting;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Commands;
using Musterdepot.Core.Domain;

namespace Musterdepot.App.ViewModels;

/// <summary>Ein KI-Client in den Einstellungen: Modus und Rechte setzen (nur hier, SetClientModeCommand).</summary>
public sealed partial class ClientRowViewModel : ObservableObject
{
    public ClientRowViewModel(AiClientConfig c)
    {
        ClientId = c.ClientId;
        Name = c.Name;
        Created = c.CreatedUtc == default ? "" : c.CreatedUtc.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.CurrentCulture);
        Enabled = c.Enabled;
        _mode = c.Mode;
        _canRead = c.Scopes.HasFlag(ClientScopes.Read);
        _canTrade = c.Scopes.HasFlag(ClientScopes.Trade);
        _canChangeSettings = c.Scopes.HasFlag(ClientScopes.Settings);
        OriginalMode = c.Mode;
        OriginalScopes = c.Scopes;
    }

    public string ClientId { get; }
    public string Name { get; }
    public string Created { get; }
    public bool Enabled { get; }
    public ClientMode OriginalMode { get; }
    public ClientScopes OriginalScopes { get; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsDirty))] private ClientMode _mode;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsDirty))] private bool _canRead;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsDirty))] private bool _canTrade;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsDirty))] private bool _canChangeSettings;

    public ClientScopes Scopes =>
        (CanRead ? ClientScopes.Read : ClientScopes.None) |
        (CanTrade ? ClientScopes.Trade : ClientScopes.None) |
        (CanChangeSettings ? ClientScopes.Settings : ClientScopes.None);

    public bool IsDirty => Mode != OriginalMode || Scopes != OriginalScopes;
}

/// <summary>
/// Einstellungen: Datenquellen (inkl. Fake), MCP an/aus und Port, Tokens je KI-Client, Modus/Rechte, Limits, Gebühren,
/// Startkapital/Einzahlung, Autostart, Darstellung.
/// </summary>
public sealed partial class SettingsViewModel(
    AppServices services,
    IUiDispatcher ui,
    IDialogService dialogs,
    AutostartService autostart,
    RuntimeController runtime,
    ThemeService theme,
    BootstrapResult bootstrap,
    ILogger<SettingsViewModel> logger) : PageViewModel
{
    private readonly List<IDisposable> _subscriptions = [];
    private AppSettings _current = bootstrap.Settings;

    public override AppPage Page => AppPage.Settings;
    public override string Title => "Einstellungen";
    public override string Glyph => "";

    public RuntimeController Runtime => runtime;
    public ObservableCollection<ClientRowViewModel> Clients { get; } = [];

    public IReadOnlyList<Option<ClientMode>> Modes { get; } =
    [
        new(ClientMode.ReadOnly, Labels.Mode(ClientMode.ReadOnly)),
        new(ClientMode.Confirm, Labels.Mode(ClientMode.Confirm)),
        new(ClientMode.Autonomous, Labels.Mode(ClientMode.Autonomous)),
    ];

    public IReadOnlyList<Option<string>> Themes { get; } = [new("system", "wie Windows"), new("light", "hell"), new("dark", "dunkel")];

    public IReadOnlyList<Option<FeeModelKind>> FeeKinds { get; } =
    [
        new(FeeModelKind.None, "keine"), new(FeeModelKind.Fixed, "fest je Order"), new(FeeModelKind.Percent, "prozentual (Maker/Taker)"),
        new(FeeModelKind.Combined, "fest + prozentual"),
    ];

    // ---- Allgemein / Datenquellen / MCP ----
    [ObservableProperty] private bool _krakenEnabled;
    [ObservableProperty] private bool _bitvavoEnabled;
    [ObservableProperty] private bool _coinbaseEnabled;
    [ObservableProperty] private bool _gettexEnabled;
    [ObservableProperty] private bool _cboeEnabled;
    [ObservableProperty] private bool _ecbEnabled;
    [ObservableProperty] private bool _useFakeData;
    [ObservableProperty] private bool _mcpEnabled;
    [ObservableProperty] private string _mcpPort = "";
    [ObservableProperty] private bool _autostartEnabled;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private Option<string>? _selectedTheme;
    [ObservableProperty] private string _initialCash = "";
    [ObservableProperty] private string _depositAmount = "";

    // ---- Gebühren ----
    [ObservableProperty] private Option<FeeModelKind>? _cryptoFeeKind;
    [ObservableProperty] private string _cryptoFixedFee = "";
    [ObservableProperty] private string _cryptoMakerPercent = "";
    [ObservableProperty] private string _cryptoTakerPercent = "";
    [ObservableProperty] private Option<FeeModelKind>? _stockFeeKind;
    [ObservableProperty] private string _stockFixedFee = "";
    [ObservableProperty] private string _stockMakerPercent = "";
    [ObservableProperty] private string _stockTakerPercent = "";

    // ---- Limits ----
    [ObservableProperty] private string _maxOrderNotional = "";
    [ObservableProperty] private string _maxOrderPercent = "";
    [ObservableProperty] private string _maxPositionNotional = "";
    [ObservableProperty] private string _maxPositionPercent = "";
    [ObservableProperty] private string _maxDailyLoss = "";
    [ObservableProperty] private string _maxDailyLossPercent = "";
    [ObservableProperty] private string _maxOrdersPerMinuteHuman = "";
    [ObservableProperty] private string _maxOrdersPerMinuteAi = "";
    [ObservableProperty] private string _priceBandPercent = "";
    [ObservableProperty] private string _maxOpenOrders = "";
    [ObservableProperty] private string _allowedSymbols = "";

    // ---- Neuer KI-Client ----
    [ObservableProperty] private string _newClientName = "Claude Code";
    [ObservableProperty] private Option<ClientMode>? _newClientMode;
    [ObservableProperty] private bool _newClientRead = true;
    [ObservableProperty] private bool _newClientTrade = true;
    [ObservableProperty] private bool _newClientSettings;
    [ObservableProperty] private string? _createdToken;
    [ObservableProperty] private string? _createdClientName;
    [ObservableProperty] private string? _claudeCommand;
    [ObservableProperty] private string? _mcpJsonSnippet;

    public bool TokensAvailable => services.TokenService is not null;
    public bool DataSourcesNeedRestart => true;

    public override void Initialize()
    {
        NewClientMode = Modes[1];
        Load(bootstrap.Settings);
        if (services.ReadModels is { } rm)
            _subscriptions.Add(rm.Settings.Subscribe(ui, s => { if (s is not null) { _current = s; if (!IsActive) Load(s); LoadClients(); } }, TimeSpan.FromMilliseconds(300)));
        if (services.ClientRegistry is { } reg) reg.Changed += (_, _) => ui.Post(LoadClients);
        LoadClients();
    }

    protected override void OnActivated()
    {
        Load(_current);
        LoadClients();
        AutostartEnabled = autostart.IsEnabled;
    }

    private void Load(AppSettings s)
    {
        var ds = s.DataSources;
        KrakenEnabled = ds.KrakenEnabled;
        BitvavoEnabled = ds.BitvavoEnabled;
        CoinbaseEnabled = ds.CoinbaseEnabled;
        GettexEnabled = ds.GettexEnabled;
        CboeEnabled = ds.CboeEnabled;
        EcbEnabled = ds.EcbEnabled;
        UseFakeData = ds.UseFakeData;
        McpEnabled = s.McpEnabled;
        McpPort = s.McpPort.ToString(CultureInfo.InvariantCulture);
        AutostartEnabled = autostart.IsEnabled;
        MinimizeToTray = s.MinimizeToTray;
        SelectedTheme = Themes.FirstOrDefault(t => string.Equals(t.Value, s.Theme, StringComparison.OrdinalIgnoreCase)) ?? Themes[0];
        InitialCash = NumberInput.Format(s.InitialCash);

        CryptoFeeKind = FeeKinds.FirstOrDefault(k => k.Value == s.CryptoFees.Kind) ?? FeeKinds[2];
        CryptoFixedFee = NumberInput.Format(s.CryptoFees.FixedFee);
        CryptoMakerPercent = NumberInput.Format(s.CryptoFees.MakerRate * 100m);
        CryptoTakerPercent = NumberInput.Format(s.CryptoFees.TakerRate * 100m);
        StockFeeKind = FeeKinds.FirstOrDefault(k => k.Value == s.StockFees.Kind) ?? FeeKinds[1];
        StockFixedFee = NumberInput.Format(s.StockFees.FixedFee);
        StockMakerPercent = NumberInput.Format(s.StockFees.MakerRate * 100m);
        StockTakerPercent = NumberInput.Format(s.StockFees.TakerRate * 100m);

        var r = s.Risk;
        MaxOrderNotional = NumberInput.Format(r.MaxOrderNotional);
        MaxOrderPercent = NumberInput.Format(r.MaxOrderPercentOfEquity);
        MaxPositionNotional = NumberInput.Format(r.MaxPositionNotional);
        MaxPositionPercent = NumberInput.Format(r.MaxPositionPercentOfEquity);
        MaxDailyLoss = NumberInput.Format(r.MaxDailyLoss);
        MaxDailyLossPercent = NumberInput.Format(r.MaxDailyLossPercent);
        MaxOrdersPerMinuteHuman = NumberInput.Format(r.MaxOrdersPerMinuteHuman);
        MaxOrdersPerMinuteAi = NumberInput.Format(r.MaxOrdersPerMinuteAi);
        PriceBandPercent = NumberInput.Format(r.PriceBandPercent);
        MaxOpenOrders = NumberInput.Format(r.MaxOpenOrders);
        AllowedSymbols = r.AllowedSymbols is { Count: > 0 } a ? string.Join(", ", a) : "";
    }

    private void LoadClients()
    {
        var clients = services.ClientRegistry?.Clients ?? _current.AiClients;
        Clients.Clear();
        foreach (var c in clients.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)) Clients.Add(new ClientRowViewModel(c));
    }

    // ---- Speichern: allgemeine Einstellungen (UpdateSettingsCommand) ----
    [RelayCommand]
    private async Task SaveGeneral()
    {
        if (!int.TryParse(McpPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) || port is <= 1024 or > 65535)
        {
            SetStatus("MCP-Port muss zwischen 1025 und 65535 liegen.", true);
            return;
        }
        if (!NumberInput.TryParse(InitialCash, out var initialCash) || initialCash is not > 0)
        {
            SetStatus("Startkapital muss eine positive Zahl sein.", true);
            return;
        }
        if (!TryBuildFees(CryptoFeeKind, CryptoFixedFee, CryptoMakerPercent, CryptoTakerPercent, _current.CryptoFees, out var cryptoFees) ||
            !TryBuildFees(StockFeeKind, StockFixedFee, StockMakerPercent, StockTakerPercent, _current.StockFees, out var stockFees))
        {
            SetStatus("Gebühren: bitte gültige Zahlen eingeben (Prozent z. B. 0,25).", true);
            return;
        }

        // Immer auf dem neuesten Stand aufsetzen (KI-Clients, Limits könnten sich inzwischen geändert haben).
        if (services.ReadModels?.Settings.Current is { } latest) _current = latest;
        var before = _current;
        var updated = _current with
        {
            McpEnabled = McpEnabled,
            McpPort = port,
            Autostart = AutostartEnabled,
            MinimizeToTray = MinimizeToTray,
            Theme = SelectedTheme?.Value ?? "system",
            InitialCash = initialCash.Value,
            CryptoFees = cryptoFees,
            StockFees = stockFees,
            DataSources = new DataSourceSettings
            {
                KrakenEnabled = KrakenEnabled,
                BitvavoEnabled = BitvavoEnabled,
                CoinbaseEnabled = CoinbaseEnabled,
                GettexEnabled = GettexEnabled,
                CboeEnabled = CboeEnabled,
                EcbEnabled = EcbEnabled,
                UseFakeData = UseFakeData,
            },
        };

        if (autostart.IsEnabled != AutostartEnabled && !autostart.SetEnabled(AutostartEnabled))
            dialogs.Error("Autostart", "Der Autostart-Eintrag konnte nicht geändert werden (siehe Protokoll).");

        var r = await services.SendAsync(new UpdateSettingsCommand { Settings = updated, Source = CommandSource.Human });
        if (!r.Success)
        {
            SetStatus("Speichern fehlgeschlagen: " + r.Message, true);
            return;
        }
        _current = updated;
        SettingsBootstrap.SaveCache(updated);
        theme.Apply(updated.Theme);

        var notes = new List<string> { "Gespeichert." };
        if (before.DataSources != updated.DataSources) notes.Add("Datenquellen wirken nach einem Neustart des Programms.");
        if (before.McpEnabled != updated.McpEnabled || before.McpPort != updated.McpPort)
        {
            await runtime.RestartMcpAsync(updated.McpEnabled);
            notes.Add(updated.McpEnabled ? $"MCP-Server neu gestartet ({runtime.McpStatus})." : "MCP-Server gestoppt.");
            if (before.McpPort != updated.McpPort) notes.Add("Port geändert: KI-Clients mit der neuen URL neu einrichten.");
        }
        if (before.InitialCash != updated.InitialCash) notes.Add("Startkapital gilt für neu eröffnete Konten; für mehr Geld „Einzahlen“ nutzen.");
        SetStatus(string.Join(" ", notes));
    }

    private static bool TryBuildFees(Option<FeeModelKind>? kind, string fixedText, string makerText, string takerText, FeeModelConfig current, out FeeModelConfig result)
    {
        result = current;
        if (!NumberInput.TryParse(fixedText, out var fix) || !NumberInput.TryParse(makerText, out var maker) || !NumberInput.TryParse(takerText, out var taker))
            return false;
        if (fix < 0 || maker < 0 || taker < 0) return false;
        result = current with
        {
            Kind = kind?.Value ?? current.Kind,
            FixedFee = fix ?? 0m,
            MakerRate = (maker ?? 0m) / 100m,
            TakerRate = (taker ?? 0m) / 100m,
        };
        return true;
    }

    // ---- Limits (UpdateRiskLimitsCommand, nur Mensch) ----
    [RelayCommand]
    private async Task SaveRisk()
    {
        decimal? D(string text, string label, ref string? err)
        {
            if (NumberInput.TryParse(text, out var v) && v is null or >= 0) return v;
            err ??= label;
            return null;
        }

        if (services.ReadModels?.Settings.Current is { } latest) _current = latest;
        string? error = null;
        var risk = _current.Risk with
        {
            MaxOrderNotional = D(MaxOrderNotional, "max. Ordergröße", ref error),
            MaxOrderPercentOfEquity = D(MaxOrderPercent, "max. Ordergröße %", ref error),
            MaxPositionNotional = D(MaxPositionNotional, "max. Positionsgröße", ref error),
            MaxPositionPercentOfEquity = D(MaxPositionPercent, "max. Positionsgröße %", ref error),
            MaxDailyLoss = D(MaxDailyLoss, "max. Tagesverlust", ref error),
            MaxDailyLossPercent = D(MaxDailyLossPercent, "max. Tagesverlust %", ref error),
            MaxOrdersPerMinuteHuman = (int)(D(MaxOrdersPerMinuteHuman, "Orders/Min Mensch", ref error) ?? _current.Risk.MaxOrdersPerMinuteHuman),
            MaxOrdersPerMinuteAi = (int)(D(MaxOrdersPerMinuteAi, "Orders/Min KI", ref error) ?? _current.Risk.MaxOrdersPerMinuteAi),
            PriceBandPercent = D(PriceBandPercent, "Preisband", ref error) ?? _current.Risk.PriceBandPercent,
            MaxOpenOrders = (int)(D(MaxOpenOrders, "max. offene Orders", ref error) ?? _current.Risk.MaxOpenOrders),
            AllowedSymbols = string.IsNullOrWhiteSpace(AllowedSymbols)
                ? null
                : AllowedSymbols.Split([',', ';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(x => x.ToUpperInvariant()).Distinct().ToList(),
        };
        if (error is not null)
        {
            SetStatus($"Limits: „{error}“ ist keine gültige Zahl ≥ 0.", true);
            return;
        }
        if (risk.MaxOrdersPerMinuteAi > risk.MaxOrdersPerMinuteHuman &&
            !dialogs.Confirm("Limits", "Die KI darf mehr Orders pro Minute als ein Mensch. Wirklich speichern?", warning: true))
            return;

        var r = await services.SendAsync(new UpdateRiskLimitsCommand { Risk = risk, Source = CommandSource.Human });
        if (r.Success) _current = _current with { Risk = risk };
        SetStatus(r.Success ? "Limits gespeichert. Sie gelten sofort für Mensch und KI." : "Limits nicht gespeichert: " + r.Message, !r.Success);
    }

    [RelayCommand]
    private async Task Deposit()
    {
        if (!NumberInput.TryParse(DepositAmount, out var amount) || amount is not > 0)
        {
            SetStatus("Einzahlung: positiven Betrag eingeben.", true);
            return;
        }
        if (!dialogs.Confirm("Einzahlen", $"{DisplayFormat.Money(amount)} Spielgeld einzahlen?")) return;
        var r = await services.SendAsync(new DepositCashCommand { Amount = amount.Value, Note = "Einzahlung über die Oberfläche", Source = CommandSource.Human });
        if (r.Success) DepositAmount = "";
        SetStatus(r.Success ? $"{DisplayFormat.Money(amount)} eingezahlt." : "Einzahlung fehlgeschlagen: " + r.Message, !r.Success);
    }

    // ---- KI-Clients ----
    [RelayCommand]
    private async Task CreateClient()
    {
        if (services.TokenService is not { } tokens)
        {
            SetStatus("Token-Dienst nicht verfügbar.", true);
            return;
        }
        var name = NewClientName.Trim();
        if (name.Length == 0)
        {
            SetStatus("Bitte einen Namen für den KI-Client eingeben.", true);
            return;
        }
        var scopes = (NewClientRead ? ClientScopes.Read : 0) | (NewClientTrade ? ClientScopes.Trade : 0) | (NewClientSettings ? ClientScopes.Settings : 0);
        if (scopes == ClientScopes.None)
        {
            SetStatus("Mindestens ein Recht auswählen.", true);
            return;
        }
        try
        {
            var (client, token) = await tokens.CreateClientAsync(name, scopes, NewClientMode?.Value ?? ClientMode.Confirm);
            CreatedClientName = client.Name;
            CreatedToken = token;
            var url = services.McpHost?.Endpoint?.ToString() ?? $"http://127.0.0.1:{_current.McpPort}/mcp";
            ClaudeCommand = $"claude mcp add --transport http musterdepot {url} --header \"Authorization: Bearer {token}\" --scope user";
            McpJsonSnippet =
                "{\n  \"mcpServers\": {\n    \"musterdepot\": {\n      \"url\": \"" + url + "\",\n      \"headers\": { \"Authorization\": \"Bearer " + token + "\" }\n    }\n  }\n}";
            SetStatus($"KI-Client „{client.Name}“ angelegt. Das Token wird nur jetzt angezeigt.");
            LoadClients();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "KI-Client konnte nicht angelegt werden");
            SetStatus("Anlegen fehlgeschlagen: " + ex.GetBaseException().Message, true);
        }
    }

    [RelayCommand]
    private void CopyToken()
    {
        if (CreatedToken is { } t) { dialogs.CopyToClipboard(t); SetStatus("Token kopiert."); }
    }

    [RelayCommand]
    private void CopyClaudeCommand()
    {
        if (ClaudeCommand is { } c) { dialogs.CopyToClipboard(c); SetStatus("Befehl kopiert – im Terminal einfügen."); }
    }

    [RelayCommand]
    private void CopyMcpJson()
    {
        if (McpJsonSnippet is { } j) { dialogs.CopyToClipboard(j); SetStatus("mcp.json-Eintrag kopiert."); }
    }

    /// <summary>Token aus dem Speicher der Oberfläche entfernen (es lässt sich danach nicht mehr anzeigen).</summary>
    [RelayCommand]
    private void DismissToken()
    {
        CreatedToken = null;
        ClaudeCommand = null;
        McpJsonSnippet = null;
        CreatedClientName = null;
    }

    [RelayCommand]
    private async Task ApplyClientMode(ClientRowViewModel? row)
    {
        if (row is null) return;
        if (row.Mode == ClientMode.Autonomous && row.OriginalMode != ClientMode.Autonomous &&
            !dialogs.Confirm("Autonomer Modus",
                $"„{row.Name}“ darf dann ohne Rückfrage handeln – nur innerhalb der Limits.\n\nWirklich aktivieren?", warning: true))
            return;
        var r = await services.SendAsync(new SetClientModeCommand
        {
            TargetClientId = row.ClientId,
            Mode = row.Mode,
            Scopes = row.Scopes,
            Source = CommandSource.Human,
        });
        SetStatus(r.Success ? $"„{row.Name}“: {Labels.Mode(row.Mode)}, Rechte {AiActivityViewModel.ScopesText(row.Scopes)}." : "Ändern fehlgeschlagen: " + r.Message, !r.Success);
        if (r.Success) LoadClients();
    }

    [RelayCommand]
    private async Task RevokeClient(ClientRowViewModel? row)
    {
        if (row is null || services.TokenService is not { } tokens) return;
        if (!dialogs.Confirm("Token widerrufen", $"Token von „{row.Name}“ widerrufen? Der Client kann sich danach nicht mehr verbinden.", warning: true))
            return;
        try
        {
            await tokens.RevokeAsync(row.ClientId);
            SetStatus($"Token von „{row.Name}“ widerrufen.");
            LoadClients();
        }
        catch (Exception ex)
        {
            SetStatus("Widerrufen fehlgeschlagen: " + ex.GetBaseException().Message, true);
        }
    }

    [RelayCommand]
    private void OpenLogs()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.LogsDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus("Ordner konnte nicht geöffnet werden: " + ex.Message, true);
        }
    }

    public string DataDirectory => AppPaths.DataDirectory;
}
