using Musterdepot.Core.Domain;

namespace Musterdepot.Core.Commands;

/// <summary>
/// Schnittstelle B: feste Befehlsliste. Oberfläche und MCP rufen nur diese Befehle über <see cref="ICommandBus"/> auf.
/// Jeder Befehl trägt Source (Mensch oder KI) und einen IdempotencyKey.
/// </summary>
public interface ICommand
{
    CommandSource Source { get; }
    string IdempotencyKey { get; }
    /// <summary>KI-Client-ID (bei Source=Ai) oder "ui".</summary>
    string ClientId { get; }
    /// <summary>Benötigter Berechtigungsbereich.</summary>
    ClientScopes RequiredScope { get; }
    /// <summary>Hat der Befehl eine Nebenwirkung (Schreibaktion)?</summary>
    bool IsWrite { get; }
    /// <summary>Darf nur von einem Menschen in der Oberfläche ausgeführt werden.</summary>
    bool HumanOnly { get; }
}

public abstract record CommandBase : ICommand
{
    public CommandSource Source { get; init; } = CommandSource.Human;
    public string IdempotencyKey { get; init; } = Guid.NewGuid().ToString("N");
    public string ClientId { get; init; } = "ui";
    public string? AccountId { get; init; }
    public virtual ClientScopes RequiredScope => ClientScopes.Trade;
    public virtual bool IsWrite => true;
    public virtual bool HumanOnly => false;
}

// ---- Orders ----
public sealed record PlaceOrderCommand : CommandBase
{
    public required string Symbol { get; init; }
    public required OrderSide Side { get; init; }
    public required OrderType Type { get; init; }
    public decimal? Quantity { get; init; }
    /// <summary>Betrag in EUR statt Stückzahl.</summary>
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
    /// <summary>Nur Vorschau mit Gebühren und Risikoprüfung, keine Order.</summary>
    public bool DryRun { get; init; }
    public override bool IsWrite => !DryRun;
    public override ClientScopes RequiredScope => DryRun ? ClientScopes.Read : ClientScopes.Trade;
}

public sealed record ModifyOrderCommand : CommandBase
{
    public required string OrderId { get; init; }
    public decimal? NewQuantity { get; init; }
    public decimal? NewLimitPrice { get; init; }
    public decimal? NewStopPrice { get; init; }
}

public sealed record CancelOrderCommand : CommandBase
{
    public required string OrderId { get; init; }
}

public sealed record CancelAllOrdersCommand : CommandBase
{
    public string? Symbol { get; init; }
}

// ---- Watchlists ----
public sealed record CreateWatchlistCommand : CommandBase
{
    public required string Name { get; init; }
    public IReadOnlyList<string> Symbols { get; init; } = [];
    public override ClientScopes RequiredScope => ClientScopes.Read;
}

public sealed record AddToWatchlistCommand : CommandBase
{
    public required string WatchlistId { get; init; }
    public required string Symbol { get; init; }
    public override ClientScopes RequiredScope => ClientScopes.Read;
}

public sealed record RemoveFromWatchlistCommand : CommandBase
{
    public required string WatchlistId { get; init; }
    public required string Symbol { get; init; }
    public override ClientScopes RequiredScope => ClientScopes.Read;
}

public sealed record DeleteWatchlistCommand : CommandBase
{
    public required string WatchlistId { get; init; }
    public override ClientScopes RequiredScope => ClientScopes.Read;
}

// ---- Alarme ----
public enum AlertCondition { PriceAbove, PriceBelow, ChangePercentAbove, ChangePercentBelow }

public sealed record CreateAlertCommand : CommandBase
{
    public required string Symbol { get; init; }
    public required AlertCondition Condition { get; init; }
    public required decimal Threshold { get; init; }
    public string? Note { get; init; }
    public override ClientScopes RequiredScope => ClientScopes.Read;
}

public sealed record DeleteAlertCommand : CommandBase
{
    public required string AlertId { get; init; }
    public override ClientScopes RequiredScope => ClientScopes.Read;
}

// ---- Einstellungen und Betrieb ----
/// <summary>KI darf Einstellungen ändern, aber nicht Limits und Modus (die Felder werden für KI ignoriert/abgelehnt).</summary>
public sealed record UpdateSettingsCommand : CommandBase
{
    public required AppSettings Settings { get; init; }
    public override ClientScopes RequiredScope => ClientScopes.Settings;
}

/// <summary>Risiko-Limits ändern – nur Mensch.</summary>
public sealed record UpdateRiskLimitsCommand : CommandBase
{
    public required RiskConfig Risk { get; init; }
    public override bool HumanOnly => true;
    public override ClientScopes RequiredScope => ClientScopes.Settings;
}

/// <summary>Modus eines KI-Clients setzen – nur Mensch.</summary>
public sealed record SetClientModeCommand : CommandBase
{
    public required string TargetClientId { get; init; }
    public required ClientMode Mode { get; init; }
    public ClientScopes? Scopes { get; init; }
    public override bool HumanOnly => true;
    public override ClientScopes RequiredScope => ClientScopes.Settings;
}

/// <summary>Not-Aus auslösen – KI darf das.</summary>
public sealed record EngageKillSwitchCommand : CommandBase
{
    public required string Reason { get; init; }
    public override ClientScopes RequiredScope => ClientScopes.Read;
}

/// <summary>Not-Aus lösen – nur per Klick im Programm.</summary>
public sealed record ReleaseKillSwitchCommand : CommandBase
{
    public override bool HumanOnly => true;
}

public sealed record DepositCashCommand : CommandBase
{
    public required decimal Amount { get; init; }
    public string? Note { get; init; }
    public override bool HumanOnly => true;
}

public sealed record ResolveApprovalCommand : CommandBase
{
    public required string ApprovalId { get; init; }
    public required bool Approved { get; init; }
    public override bool HumanOnly => true;
}

public enum CommandStatus { Executed, Previewed, PendingApproval, Rejected, Denied, Duplicate, Failed }

public sealed record CommandResult
{
    public required CommandStatus Status { get; init; }
    public string? Message { get; init; }
    /// <summary>Ergebnisdaten (z. B. Order, OrderPreview, Watchlist).</summary>
    public object? Data { get; init; }
    public string? AuditId { get; init; }
    public string? ApprovalId { get; init; }
    public bool Success => Status is CommandStatus.Executed or CommandStatus.Previewed or CommandStatus.Duplicate;

    public static CommandResult Ok(object? data = null, string? message = null) => new() { Status = CommandStatus.Executed, Data = data, Message = message };
    public static CommandResult Fail(CommandStatus status, string message) => new() { Status = status, Message = message };
}

/// <summary>Einzige Befehlsschicht: Prüfung, Risikolimits, Freigabe, Protokoll.</summary>
public interface ICommandBus
{
    Task<CommandResult> SendAsync(ICommand command, CancellationToken ct = default);
}
