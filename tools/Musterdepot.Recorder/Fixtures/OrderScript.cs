using System.Text.Json;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Commands;
using Musterdepot.Core.Domain;
using Musterdepot.Core.Serialization;

namespace Musterdepot.Recorder.Fixtures;

/// <summary>
/// Orderskript eines Replay-Szenarios (<c>*.orders.json</c>, Format in docs/Tests.md). Enthält alles, was neben der
/// Marktdaten-Aufzeichnung für einen Golden Replay nötig ist: Instrumente, Konten und zeitgestempelte Befehle.
/// Zeitpunkte beziehen sich auf die Replay-Uhr (<c>ReceivedUtc</c> der Marktereignisse).
/// </summary>
public sealed record OrderScript
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public required string Scenario { get; init; }
    public string? Description { get; init; }
    /// <summary>Dateiname der Aufzeichnung, relativ zum Orderskript.</summary>
    public required string Replay { get; init; }
    /// <summary>Seed des Generators (nur Information; 0 = echte Aufzeichnung).</summary>
    public int Seed { get; init; }
    public IReadOnlyList<Instrument> Instruments { get; init; } = [];
    public IReadOnlyList<OrderScriptAccount> Accounts { get; init; } = [];
    public IReadOnlyList<OrderScriptStep> Steps { get; init; } = [];
}

public sealed record OrderScriptAccount
{
    public required string AccountId { get; init; }
    public required string Name { get; init; }
    public required decimal InitialCash { get; init; }
    public string BaseCurrency { get; init; } = "EUR";
    public RiskConfig? Risk { get; init; }
    public FeeModelConfig? CryptoFees { get; init; }
    public FeeModelConfig? StockFees { get; init; }

    public OpenAccountRequest ToRequest() => new()
    {
        AccountId = AccountId,
        Name = Name,
        InitialCash = InitialCash,
        BaseCurrency = BaseCurrency,
        Risk = Risk,
        CryptoFees = CryptoFees,
        StockFees = StockFees,
    };
}

public enum OrderScriptAction { PlaceOrder, Cancel, Modify }

public sealed record OrderScriptStep
{
    /// <summary>Zeitpunkt auf der Replay-Uhr, zu dem der Befehl abgesetzt wird.</summary>
    public required DateTimeOffset AtUtc { get; init; }
    public required OrderScriptAction Action { get; init; }
    public required string AccountId { get; init; }
    public CommandSource Source { get; init; } = CommandSource.Human;
    /// <summary>KI-Client-Name (bei Source=Ai) bzw. "ui".</summary>
    public string? ClientName { get; init; }
    public OrderScriptPlaceOrder? PlaceOrder { get; init; }
    public OrderScriptCancel? Cancel { get; init; }
    public OrderScriptModify? Modify { get; init; }
    /// <summary>Erwartung zur Plausibilisierung (die Golden-Datei ist die eigentliche Prüfung).</summary>
    public OrderScriptExpectation? Expect { get; init; }
    public string? Note { get; init; }

    public string EffectiveClientId => ClientName ?? (Source == CommandSource.Ai ? "ai-test" : "ui");

    /// <summary>Engine-Befehl. <paramref name="resolveOrderId"/> löst eine ClientOrderId in die OrderId der Engine auf.</summary>
    public EngineCommand ToEngineRequest(Func<string, string?> resolveOrderId) => Action switch
    {
        OrderScriptAction.PlaceOrder => PlaceOrder!.ToRequest(AccountId, Source, ClientName),
        OrderScriptAction.Cancel => new CancelOrderRequest
        {
            AccountId = AccountId,
            Source = Source,
            ClientName = ClientName,
            OrderId = ResolveTarget(Cancel!.OrderId, Cancel.ClientOrderId, resolveOrderId),
            Reason = Cancel.Reason,
        },
        OrderScriptAction.Modify => new ModifyOrderRequest
        {
            AccountId = AccountId,
            Source = Source,
            ClientName = ClientName,
            OrderId = ResolveTarget(Modify!.OrderId, Modify.ClientOrderId, resolveOrderId),
            NewQuantity = Modify.NewQuantity,
            NewLimitPrice = Modify.NewLimitPrice,
            NewStopPrice = Modify.NewStopPrice,
        },
        _ => throw new InvalidOperationException($"Unbekannte Aktion {Action}"),
    };

    /// <summary>Befehl für die Befehlsschicht (Vergleich Oberfläche ↔ MCP). IdempotencyKey = ClientOrderId bzw. Schrittschlüssel.</summary>
    public ICommand ToCommand(Func<string, string?> resolveOrderId, int stepIndex = 0) => Action switch
    {
        OrderScriptAction.PlaceOrder => PlaceOrder!.ToCommand(AccountId, Source, EffectiveClientId),
        OrderScriptAction.Cancel => new CancelOrderCommand
        {
            AccountId = AccountId,
            Source = Source,
            ClientId = EffectiveClientId,
            IdempotencyKey = $"cancel-{stepIndex:D3}-{Cancel!.ClientOrderId ?? Cancel.OrderId}",
            OrderId = ResolveTarget(Cancel.OrderId, Cancel.ClientOrderId, resolveOrderId),
        },
        OrderScriptAction.Modify => new ModifyOrderCommand
        {
            AccountId = AccountId,
            Source = Source,
            ClientId = EffectiveClientId,
            IdempotencyKey = $"modify-{stepIndex:D3}-{Modify!.ClientOrderId ?? Modify.OrderId}",
            OrderId = ResolveTarget(Modify.OrderId, Modify.ClientOrderId, resolveOrderId),
            NewQuantity = Modify.NewQuantity,
            NewLimitPrice = Modify.NewLimitPrice,
            NewStopPrice = Modify.NewStopPrice,
        },
        _ => throw new InvalidOperationException($"Unbekannte Aktion {Action}"),
    };

    private static string ResolveTarget(string? orderId, string? clientOrderId, Func<string, string?> resolve)
    {
        if (orderId is not null) return orderId;
        if (clientOrderId is null) throw new InvalidOperationException("Weder orderId noch clientOrderId angegeben.");
        return resolve(clientOrderId) ?? throw new InvalidOperationException($"Order mit ClientOrderId '{clientOrderId}' unbekannt.");
    }
}

/// <summary>Felder wie <see cref="PlaceOrderRequest"/> (ohne AccountId/Source/ClientName, die am Schritt stehen) plus DryRun.</summary>
public sealed record OrderScriptPlaceOrder
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
    public bool DryRun { get; init; }

    public PlaceOrderRequest ToRequest(string accountId, CommandSource source, string? clientName) => new()
    {
        AccountId = accountId,
        Source = source,
        ClientName = clientName,
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

    public PlaceOrderCommand ToCommand(string accountId, CommandSource source, string clientId) => new()
    {
        AccountId = accountId,
        Source = source,
        ClientId = clientId,
        IdempotencyKey = ClientOrderId,
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
        DryRun = DryRun,
    };
}

/// <summary>Ziel über <c>clientOrderId</c> (empfohlen, deterministisch) oder <c>orderId</c>.</summary>
public sealed record OrderScriptCancel
{
    public string? ClientOrderId { get; init; }
    public string? OrderId { get; init; }
    public string Reason { get; init; } = "user";
}

public sealed record OrderScriptModify
{
    public string? ClientOrderId { get; init; }
    public string? OrderId { get; init; }
    public decimal? NewQuantity { get; init; }
    public decimal? NewLimitPrice { get; init; }
    public decimal? NewStopPrice { get; init; }
}

/// <summary>Erwartungen an die (Ziel-)Order am Ende des Replays. Alle Felder optional.</summary>
public sealed record OrderScriptExpectation
{
    /// <summary>Wird der Befehl angenommen (Risiko-Gate, Validierung)?</summary>
    public bool? Accepted { get; init; }
    /// <summary>Status der Order am Replay-Ende (bei Cancel/Modify: der Zielorder).</summary>
    public OrderStatus? FinalStatus { get; init; }
    public bool? ExecutedDuringGap { get; init; }
    public decimal? MinFillPrice { get; init; }
    public decimal? MaxFillPrice { get; init; }
    public FillQuality? FillQuality { get; init; }
    /// <summary>Alle Fills müssen gegen Marktdaten mit AsOfUtc &gt; diesem Zeitpunkt erfolgen („kein Fill vor Ordereingang“).</summary>
    public DateTimeOffset? FillsOnlyFromMarketDataAfterUtc { get; init; }
}

public static class OrderScriptJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonDefaults.Options)
    {
        WriteIndented = true,
        NewLine = "\n",
        // Umlaute in Notizen lesbar lassen (Datei ist UTF-8).
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(OrderScript script) => JsonSerializer.Serialize(script, Options) + "\n";

    public static OrderScript Deserialize(string json) =>
        JsonSerializer.Deserialize<OrderScript>(json, Options) ?? throw new JsonException("Leeres Orderskript.");

    public static OrderScript Load(string path) => Deserialize(File.ReadAllText(path));

    public static async Task SaveAsync(string path, OrderScript script, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, Serialize(script), new System.Text.UTF8Encoding(false), ct);
    }
}
