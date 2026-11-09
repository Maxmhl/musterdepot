using System.Text.Json;
using System.Text.Json.Serialization;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;
using Musterdepot.Core.Serialization;

namespace Musterdepot.Engine.Backtest;

/// <summary>Ein zeitgesteuerter Schritt eines Orderskripts (Golden Replays, Backtests).</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(PlaceOrderStep), "place")]
[JsonDerivedType(typeof(CancelOrderStep), "cancel")]
[JsonDerivedType(typeof(ModifyOrderStep), "modify")]
[JsonDerivedType(typeof(DepositStep), "deposit")]
[JsonDerivedType(typeof(KillSwitchStep), "killswitch")]
[JsonDerivedType(typeof(UpdateRiskStep), "risk")]
public abstract record ScriptStep
{
    /// <summary>Simulierte Uhrzeit, zu der der Schritt ausgeführt wird.</summary>
    public required DateTimeOffset AtUtc { get; init; }
    public CommandSource Source { get; init; } = CommandSource.Human;
    public string? ClientName { get; init; }

    public abstract EngineCommand? ToCommand(string accountId, ITradingEngine engine);
}

public sealed record PlaceOrderStep : ScriptStep
{
    public required string ClientOrderId { get; init; }
    public required string Symbol { get; init; }
    public required OrderSide Side { get; init; }
    public required OrderType Type { get; init; }
    public decimal? Quantity { get; init; }
    public decimal? Notional { get; init; }
    public decimal? LimitPrice { get; init; }
    public decimal? StopPrice { get; init; }
    public decimal? TrailAmount { get; init; }
    public decimal? TrailPercent { get; init; }
    public TimeInForce TimeInForce { get; init; } = TimeInForce.Day;
    public DateTimeOffset? ExpiresUtc { get; init; }
    public string? StrategyTag { get; init; }
    public decimal? TakeProfitPrice { get; init; }
    public decimal? StopLossPrice { get; init; }
    public string? OcoGroupId { get; init; }

    public PlaceOrderRequest ToRequest(string accountId) => new()
    {
        AccountId = accountId,
        Source = Source,
        ClientName = ClientName,
        ClientOrderId = ClientOrderId,
        Symbol = Symbol,
        Side = Side,
        Type = Type,
        Quantity = Quantity,
        Notional = Notional,
        LimitPrice = LimitPrice,
        StopPrice = StopPrice,
        TrailAmount = TrailAmount,
        TrailPercent = TrailPercent,
        TimeInForce = TimeInForce,
        ExpiresUtc = ExpiresUtc,
        StrategyTag = StrategyTag,
        TakeProfitPrice = TakeProfitPrice,
        StopLossPrice = StopLossPrice,
        OcoGroupId = OcoGroupId,
    };

    public override EngineCommand ToCommand(string accountId, ITradingEngine engine) => ToRequest(accountId);
}

/// <summary>Storniert die Order mit dieser ClientOrderId (Order-IDs werden erst zur Laufzeit vergeben).</summary>
public sealed record CancelOrderStep : ScriptStep
{
    public required string ClientOrderId { get; init; }
    public string Reason { get; init; } = "script";

    public override EngineCommand? ToCommand(string accountId, ITradingEngine engine)
        => engine.GetOrderByClientOrderId(accountId, ClientOrderId) is { } o
            ? new CancelOrderRequest { AccountId = accountId, OrderId = o.OrderId, Reason = Reason, Source = Source, ClientName = ClientName }
            : null;
}

public sealed record ModifyOrderStep : ScriptStep
{
    public required string ClientOrderId { get; init; }
    public decimal? NewQuantity { get; init; }
    public decimal? NewLimitPrice { get; init; }
    public decimal? NewStopPrice { get; init; }

    public override EngineCommand? ToCommand(string accountId, ITradingEngine engine)
        => engine.GetOrderByClientOrderId(accountId, ClientOrderId) is { } o
            ? new ModifyOrderRequest { AccountId = accountId, OrderId = o.OrderId, NewQuantity = NewQuantity, NewLimitPrice = NewLimitPrice, NewStopPrice = NewStopPrice, Source = Source, ClientName = ClientName }
            : null;
}

public sealed record DepositStep : ScriptStep
{
    public required decimal Amount { get; init; }
    public string? Note { get; init; }
    public override EngineCommand ToCommand(string accountId, ITradingEngine engine)
        => new DepositCashRequest { AccountId = accountId, Amount = Amount, Note = Note, Source = Source, ClientName = ClientName };
}

public sealed record KillSwitchStep : ScriptStep
{
    public bool Engage { get; init; } = true;
    public string Reason { get; init; } = "script";
    public override EngineCommand ToCommand(string accountId, ITradingEngine engine) => Engage
        ? new EngageKillSwitchRequest { AccountId = accountId, Reason = Reason, Source = Source, ClientName = ClientName }
        : new ReleaseKillSwitchRequest { AccountId = accountId, ReleasedBy = ClientName ?? "script", Source = Source, ClientName = ClientName };
}

public sealed record UpdateRiskStep : ScriptStep
{
    public required RiskConfig Risk { get; init; }
    public override EngineCommand ToCommand(string accountId, ITradingEngine engine)
        => new UpdateRiskConfigRequest { AccountId = accountId, Risk = Risk, Source = Source, ClientName = ClientName };
}

/// <summary>Orderskript: Kontoeinstellungen + zeitgesteuerte Schritte. JSON-Format über <see cref="JsonDefaults.Options"/>.</summary>
public sealed record BacktestScript
{
    public string AccountId { get; init; } = "backtest";
    public string Name { get; init; } = "Backtest";
    public decimal InitialCash { get; init; } = 100_000m;
    /// <summary>Zeitpunkt der Kontoeröffnung; ohne Angabe der früheste Zeitpunkt aus Daten und Skript.</summary>
    public DateTimeOffset? StartUtc { get; init; }
    public RiskConfig? Risk { get; init; }
    public FeeModelConfig? CryptoFees { get; init; }
    public FeeModelConfig? StockFees { get; init; }
    public IReadOnlyList<ScriptStep> Steps { get; init; } = [];

    private static readonly JsonSerializerOptions Indented = new(JsonDefaults.Options) { WriteIndented = true };

    public static BacktestScript FromJson(string json) => JsonSerializer.Deserialize<BacktestScript>(json, JsonDefaults.Options)!;
    public static BacktestScript Load(string path) => FromJson(File.ReadAllText(path));
    public string ToJson() => JsonSerializer.Serialize(this, Indented);
}
