namespace Musterdepot.Core.Domain;

/// <summary>Risikoregeln, geprüft vor jeder Order, egal ob Mensch oder KI.</summary>
public sealed record RiskConfig
{
    /// <summary>Max. Ordergröße absolut (EUR), null = unbegrenzt.</summary>
    public decimal? MaxOrderNotional { get; init; } = 10_000m;
    /// <summary>Max. Ordergröße in % des Depotwerts.</summary>
    public decimal? MaxOrderPercentOfEquity { get; init; } = 25m;
    public decimal? MaxPositionNotional { get; init; } = 25_000m;
    public decimal? MaxPositionPercentOfEquity { get; init; } = 50m;
    /// <summary>Max. Tagesverlust (EUR, positiv angegeben). Danach nur noch reduzierende Orders.</summary>
    public decimal? MaxDailyLoss { get; init; } = 1_000m;
    public decimal? MaxDailyLossPercent { get; init; }
    public int MaxOrdersPerMinuteHuman { get; init; } = 30;
    /// <summary>Für die KI strenger.</summary>
    public int MaxOrdersPerMinuteAi { get; init; } = 10;
    /// <summary>Preisband gegen Tippfehler: Limit/Stop max. x % vom letzten Kurs entfernt.</summary>
    public decimal PriceBandPercent { get; init; } = 10m;
    public int MaxOpenOrders { get; init; } = 50;
    /// <summary>Erlaubte Symbole, null/leer = alle.</summary>
    public IReadOnlyList<string>? AllowedSymbols { get; init; }
    /// <summary>Kein Leerverkauf in v1.</summary>
    public bool AllowShortSelling { get; init; }
    /// <summary>Wie lange ohne Daten, bevor Market-Orders abgelehnt werden.</summary>
    public TimeSpan MaxQuoteAge { get; init; } = TimeSpan.FromMinutes(2);

    public static RiskConfig Default { get; } = new();
}

public sealed record FeeTier(decimal FromNotional, decimal Rate);

/// <summary>Gebührenmodell: fest je Order, prozentual Maker/Taker, gestaffelt oder kombiniert.</summary>
public sealed record FeeModelConfig
{
    public FeeModelKind Kind { get; init; } = FeeModelKind.Fixed;
    /// <summary>Feste Gebühr je Order (EUR), z. B. 1 € Neobroker.</summary>
    public decimal FixedFee { get; init; } = 1m;
    /// <summary>Maker-Satz als Anteil (0.0025 = 0,25 %).</summary>
    public decimal MakerRate { get; init; }
    public decimal TakerRate { get; init; }
    /// <summary>Gestaffelte Sätze nach Order-Volumen.</summary>
    public IReadOnlyList<FeeTier> Tiers { get; init; } = [];
    public decimal MinFee { get; init; }
    public decimal? MaxFee { get; init; }

    public static FeeModelConfig NeoBroker { get; } = new() { Kind = FeeModelKind.Fixed, FixedFee = 1m };
    public static FeeModelConfig KrakenLike { get; } = new() { Kind = FeeModelKind.Percent, MakerRate = 0.0025m, TakerRate = 0.004m, FixedFee = 0 };
}

/// <summary>Annahmen für die Ausführungssimulation.</summary>
public sealed record ExecutionConfig
{
    /// <summary>Orders werden erst gegen Daten nach dieser Latenz geprüft.</summary>
    public TimeSpan Latency { get; init; } = TimeSpan.FromMilliseconds(200);
    /// <summary>Ruhende Limits füllen max. diesen Anteil des durchgehandelten Volumens.</summary>
    public decimal LimitFillVolumeShare { get; init; } = 0.25m;
    /// <summary>Angenommener Spread (in %) für verzögerte Aktien ohne Bid/Ask.</summary>
    public decimal SyntheticSpreadPercent { get; init; } = 0.10m;
}

public sealed record AiClientConfig
{
    public required string ClientId { get; init; }
    public required string Name { get; init; }
    public ClientMode Mode { get; init; } = ClientMode.Confirm;
    public ClientScopes Scopes { get; init; } = ClientScopes.Read | ClientScopes.Trade;
    /// <summary>Hash des Tokens (Token selbst liegt per DPAPI verschlüsselt im Benutzerprofil).</summary>
    public string? TokenHash { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public bool Enabled { get; init; } = true;
}

public sealed record DataSourceSettings
{
    public bool KrakenEnabled { get; init; } = true;
    public bool BitvavoEnabled { get; init; } = true;
    public bool CoinbaseEnabled { get; init; } = true;
    public bool GettexEnabled { get; init; } = true;
    public bool CboeEnabled { get; init; }
    public bool EcbEnabled { get; init; } = true;
    public bool UseFakeData { get; init; }
}

/// <summary>Programmeinstellungen (Einstellungen-Bildschirm, get_settings/update_settings).</summary>
public sealed record AppSettings
{
    public int McpPort { get; init; } = 47123;
    public bool McpEnabled { get; init; } = true;
    public bool Autostart { get; init; }
    public bool MinimizeToTray { get; init; } = true;
    public string DefaultAccountId { get; init; } = "main";
    public decimal InitialCash { get; init; } = 100_000m;
    public string BaseCurrency { get; init; } = "EUR";
    public DataSourceSettings DataSources { get; init; } = new();
    public FeeModelConfig CryptoFees { get; init; } = FeeModelConfig.KrakenLike;
    public FeeModelConfig StockFees { get; init; } = FeeModelConfig.NeoBroker;
    public RiskConfig Risk { get; init; } = RiskConfig.Default;
    public ExecutionConfig Execution { get; init; } = new();
    public IReadOnlyList<AiClientConfig> AiClients { get; init; } = [];
    /// <summary>UI-Theme, Sprache usw.</summary>
    public string Theme { get; init; } = "system";
}
