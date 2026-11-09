using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Commands;
using Musterdepot.Core.Domain;
using Musterdepot.Core.Events;
using Musterdepot.Core.Serialization;

namespace Musterdepot.Commands;

/// <summary>
/// Einzige Befehlsschicht. Pipeline je Befehl:
/// <list type="number">
/// <item>Idempotenz (nur Schreibbefehle): gleicher ClientId + IdempotencyKey → gespeichertes Ergebnis
/// (erfolgreich → Status <see cref="CommandStatus.Duplicate"/> mit gleichen Daten; abgelehnt → gleiches Ergebnis).
/// PlaceOrder zusätzlich über die ClientOrderId der Engine (überlebt Neustarts).</item>
/// <item>Autorisierung: Client bekannt/aktiv, Source passt zum Client, HumanOnly, Scopes.</item>
/// <item>Modus: Tagesverlustgrenze erreicht → KI effektiv read_only (offene KI-Orders werden storniert);
/// read_only → Schreibbefehle Denied (Not-Aus auslösen bleibt erlaubt).</item>
/// <item>Vorprüfungen/Client-Limits: Eingaben, Not-Aus aktiv, Ratenlimit pro Client, erlaubte Symbole, max. offene
/// Orders, KI darf nur eigene Orders ändern/stornieren.</item>
/// <item>Modus confirm (KI): Freigabe über <see cref="IApprovalService"/> (Timeout aus <see cref="CommandOptions"/>);
/// abgelehnt/abgelaufen → Denied.</item>
/// <item>Ausführung (Engine/Stores) und Protokoll in <see cref="IAuditLog"/>.</item>
/// </list>
/// Liefert immer ein <see cref="CommandResult"/>; Ausnahmen werden zu <see cref="CommandStatus.Failed"/>.
/// </summary>
public sealed class CommandBus : ICommandBus, IDisposable
{
    public const string SystemClientId = "system";
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    private sealed record IdempotencyEntry(string CommandType, Task<CommandResult> Result, DateTimeOffset CreatedUtc);

    private sealed class ExecutionContext
    {
        public ClientIdentity? Identity { get; set; }
        public string AccountId { get; set; } = "";
        public string? ApprovedBy { get; set; }
        public string? ApprovalId { get; set; }
        public bool DailyLossLimitHit { get; set; }
    }

    private readonly ITradingEngine _engine;
    private readonly IWatchlistStore _watchlists;
    private readonly IAlertStore _alerts;
    private readonly IAuditLog _audit;
    private readonly IApprovalService _approvals;
    private readonly ClientRegistry _clients;
    private readonly SettingsService _settings;
    private readonly ClientActivityTracker _activity;
    private readonly StoreChangeNotifier _changes;
    private readonly IUserNotifier _notifier;
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;
    private readonly CommandOptions _options;
    private readonly ILogger<CommandBus> _logger;
    private readonly ConcurrentDictionary<(string ClientId, string Key), IdempotencyEntry> _idempotency = new();
    private readonly SemaphoreSlim _dailyLossGate = new(1, 1);
    private readonly SemaphoreSlim _watchlistGate = new(1, 1);
    private long _sendCount;

    public CommandBus(
        ITradingEngine engine,
        IWatchlistStore watchlists,
        IAlertStore alerts,
        IAuditLog audit,
        IApprovalService approvals,
        ClientRegistry clients,
        SettingsService settings,
        ClientActivityTracker activity,
        StoreChangeNotifier changes,
        IUserNotifier notifier,
        IClock clock,
        IIdGenerator ids,
        CommandOptions options,
        ILogger<CommandBus> logger)
    {
        _engine = engine;
        _watchlists = watchlists;
        _alerts = alerts;
        _audit = audit;
        _approvals = approvals;
        _clients = clients;
        _settings = settings;
        _activity = activity;
        _changes = changes;
        _notifier = notifier;
        _clock = clock;
        _ids = ids;
        _options = options;
        _logger = logger;
        _engine.EventsAppended += OnEngineEvents;
    }

    public void Dispose() => _engine.EventsAppended -= OnEngineEvents;

    // ------------------------------------------------------------------ Pipeline

    public async Task<CommandResult> SendAsync(ICommand command, CancellationToken ct = default)
    {
        if (command is null) return CommandResult.Fail(CommandStatus.Rejected, "Kein Befehl angegeben.");
        var sw = Stopwatch.StartNew();
        var ctx = new ExecutionContext();
        if (Interlocked.Increment(ref _sendCount) % 256 == 0) PurgeIdempotency();

        if (!command.IsWrite || string.IsNullOrWhiteSpace(command.IdempotencyKey))
        {
            var r = await ProcessAsync(command, ctx, ct).ConfigureAwait(false);
            return await AuditAsync(command, ctx, r, sw).ConfigureAwait(false);
        }

        var key = (command.ClientId ?? "", command.IdempotencyKey);
        var tcs = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = new IdempotencyEntry(command.GetType().Name, tcs.Task, _clock.UtcNow);
        var existing = _idempotency.GetOrAdd(key, entry);
        if (!ReferenceEquals(existing, entry))
        {
            CommandResult dup;
            if (existing.CommandType != entry.CommandType)
                dup = CommandResult.Fail(CommandStatus.Rejected, $"IdempotencyKey '{command.IdempotencyKey}' wurde bereits für {existing.CommandType} verwendet.");
            else
                dup = AsDuplicate(await existing.Result.WaitAsync(ct).ConfigureAwait(false));
            return await AuditAsync(command, ctx, dup, sw).ConfigureAwait(false);
        }

        CommandResult result;
        try
        {
            result = await ProcessAsync(command, ctx, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = CommandResult.Fail(CommandStatus.Failed, $"Interner Fehler: {ex.Message}");
        }
        result = await AuditAsync(command, ctx, result, sw).ConfigureAwait(false);
        // Fehlgeschlagene (technische) Ergebnisse nicht merken – Wiederholung soll möglich sein.
        if (result.Status == CommandStatus.Failed) _idempotency.TryRemove(new KeyValuePair<(string, string), IdempotencyEntry>(key, entry));
        tcs.TrySetResult(result);
        return result;
    }

    private static CommandResult AsDuplicate(CommandResult original) =>
        original.Success
            ? original with { Status = CommandStatus.Duplicate, Message = "Bereits ausgeführt (gleicher IdempotencyKey)." + (original.Message is null ? "" : " " + original.Message) }
            : original;

    private void PurgeIdempotency()
    {
        var cutoff = _clock.UtcNow - _options.IdempotencyRetention;
        foreach (var kv in _idempotency)
            if (kv.Value.CreatedUtc < cutoff && kv.Value.Result.IsCompleted) _idempotency.TryRemove(kv);
    }

    private async Task<CommandResult> ProcessAsync(ICommand command, ExecutionContext ctx, CancellationToken ct)
    {
        try
        {
            // (b) Autorisierung
            var identity = _clients.Resolve(command.ClientId, command.Source);
            if (identity is null)
                return Denied(command.Source == CommandSource.Human && command.ClientId != ClientRegistry.UiClientId
                    ? $"Quelle 'Mensch' ist nur für die Oberfläche zulässig (Client '{command.ClientId}')."
                    : $"Unbekannter, deaktivierter oder unpassender Client '{command.ClientId}'.");
            ctx.Identity = identity;
            ctx.AccountId = (command as CommandBase)?.AccountId is { Length: > 0 } acc ? acc : _settings.Current.DefaultAccountId;
            _activity.Touch(identity.ClientId);

            if (command.HumanOnly && command.Source != CommandSource.Human)
                return Denied($"{command.GetType().Name} darf nur ein Mensch in der Oberfläche ausführen.");
            if ((identity.Scopes & command.RequiredScope) != command.RequiredScope)
                return Denied($"Client '{identity.Name}' fehlt die Berechtigung '{command.RequiredScope}'.");

            var isAi = command.Source == CommandSource.Ai;
            var isKillSwitch = command is EngageKillSwitchCommand;

            // (d) Tagesverlustgrenze → KI effektiv read_only
            var mode = identity.Mode;
            if (isAi && IsDailyLossLimitHit(ctx.AccountId))
            {
                ctx.DailyLossLimitHit = true;
                mode = ClientMode.ReadOnly;
                await EnforceDailyLossLimitAsync(ctx.AccountId, ct).ConfigureAwait(false);
            }
            if (command.IsWrite && mode == ClientMode.ReadOnly && !isKillSwitch)
                return Denied(ctx.DailyLossLimitHit
                    ? "Tagesverlustgrenze erreicht: KI-Clients sind bis zum Tageswechsel nur lesend (read_only)."
                    : $"Client '{identity.Name}' ist im Modus read_only.");

            // (c) Vorprüfungen und Client-Limits
            if (Precheck(command, ctx) is { } rejected) return rejected;

            // (e) Freigabe im Modus confirm
            if (isAi && mode == ClientMode.Confirm && command.IsWrite && !isKillSwitch
                && (command.RequiredScope & _options.ConfirmRequiredScopes) != 0)
            {
                var decided = await RequestApprovalAsync(command, ctx, ct).ConfigureAwait(false);
                ctx.ApprovalId = decided.ApprovalId;
                if (decided.State != ApprovalState.Approved)
                {
                    var msg = decided.State == ApprovalState.Expired
                        ? $"Freigabe abgelaufen ({decided.DecidedBy})."
                        : $"Freigabe abgelehnt von {decided.DecidedBy}.";
                    return Denied(msg) with { ApprovalId = decided.ApprovalId };
                }
                ctx.ApprovedBy = decided.DecidedBy;
                if (command is PlaceOrderCommand or ModifyOrderCommand && IsKillSwitchOn(ctx.AccountId))
                    return Rejected("Not-Aus ist aktiv – keine neuen Orders.") with { ApprovalId = ctx.ApprovalId };
            }

            // (f) Ausführung
            var result = await ExecuteAsync(command, ctx, ct).ConfigureAwait(false);
            return ctx.ApprovalId is null ? result : result with { ApprovalId = ctx.ApprovalId };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return CommandResult.Fail(CommandStatus.Failed, "Abgebrochen.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Befehl {Command} von {Client} fehlgeschlagen.", command.GetType().Name, command.ClientId);
            return CommandResult.Fail(CommandStatus.Failed, $"Interner Fehler: {ex.Message}");
        }
    }

    private CommandResult? Precheck(ICommand command, ExecutionContext ctx)
    {
        var identity = ctx.Identity!;
        var isAi = command.Source == CommandSource.Ai;
        switch (command)
        {
            case PlaceOrderCommand p:
            {
                if (string.IsNullOrWhiteSpace(p.Symbol)) return Rejected("Symbol fehlt.");
                if (p.Quantity is null == p.Notional is null) return Rejected("Genau eines von Quantity oder Notional angeben.");
                if (p.Quantity is <= 0 || p.Notional is <= 0) return Rejected("Menge/Betrag muss größer 0 sein.");
                if (p.Type is OrderType.Limit or OrderType.StopLimit && p.LimitPrice is not > 0) return Rejected("Limit-Preis fehlt oder ist ungültig.");
                if (p.Type is OrderType.Stop or OrderType.StopLimit && p.StopPrice is not > 0) return Rejected("Stop-Preis fehlt oder ist ungültig.");
                if (p.DryRun) return null;
                if (IsKillSwitchOn(ctx.AccountId)) return Rejected("Not-Aus ist aktiv – keine neuen Orders.");
                var risk = GetRisk(ctx.AccountId);
                if (isAi)
                {
                    if (risk.AllowedSymbols is { Count: > 0 } allowed && !allowed.Contains(p.Symbol, StringComparer.OrdinalIgnoreCase))
                        return Rejected($"Symbol {p.Symbol} ist für KI-Clients nicht erlaubt.");
                    var openOwn = SafeOrders(ctx.AccountId, openOnly: true).Count(o => IsOwnAiOrder(o, identity));
                    if (risk.MaxOpenOrders > 0 && openOwn >= risk.MaxOpenOrders)
                        return Rejected($"Max. {risk.MaxOpenOrders} offene Orders je Client erreicht.");
                }
                var perMinute = isAi ? risk.MaxOrdersPerMinuteAi : risk.MaxOrdersPerMinuteHuman;
                if (!_activity.TryRegisterOrder(identity.ClientId, perMinute))
                    return Rejected($"Ratenlimit: max. {perMinute} Orders pro Minute.");
                return null;
            }
            case ModifyOrderCommand m:
            {
                if (IsKillSwitchOn(ctx.AccountId)) return Rejected("Not-Aus ist aktiv – Orders können nicht geändert werden.");
                return CheckOrderAccess(m.OrderId, ctx);
            }
            case CancelOrderCommand c:
                return CheckOrderAccess(c.OrderId, ctx);
            case DepositCashCommand d when d.Amount <= 0:
                return Rejected("Betrag muss größer 0 sein.");
            case CreateAlertCommand a when string.IsNullOrWhiteSpace(a.Symbol):
                return Rejected("Symbol fehlt.");
            case CreateWatchlistCommand w when string.IsNullOrWhiteSpace(w.Name):
                return Rejected("Name fehlt.");
            case EngageKillSwitchCommand k when string.IsNullOrWhiteSpace(k.Reason):
                return Rejected("Begründung fehlt.");
            default:
                return null;
        }
    }

    private CommandResult? CheckOrderAccess(string orderId, ExecutionContext ctx)
    {
        var order = _engine.GetOrder(ctx.AccountId, orderId);
        if (order is null) return Rejected($"Order {orderId} nicht gefunden.");
        if (ctx.Identity!.ClientId != ClientRegistry.UiClientId && !IsOwnAiOrder(order, ctx.Identity))
            return Denied($"Order {orderId} gehört nicht zu Client '{ctx.Identity.Name}'.");
        if (!order.IsOpen) return Rejected($"Order {orderId} ist nicht mehr offen ({order.Status}).");
        return null;
    }

    private static bool IsOwnAiOrder(Order o, ClientIdentity identity)
        => o.Source == CommandSource.Ai && string.Equals(o.ClientName, identity.Name, StringComparison.Ordinal);

    // ------------------------------------------------------------------ Freigabe

    private async Task<ApprovalRequest> RequestApprovalAsync(ICommand command, ExecutionContext ctx, CancellationToken ct)
    {
        var request = new ApprovalRequest
        {
            ApprovalId = _ids.NewId("apr"),
            CreatedUtc = _clock.UtcNow,
            ClientId = ctx.Identity!.ClientId,
            ClientName = ctx.Identity.Name,
            Command = command,
            Summary = Describe(command),
        };
        var pending = _approvals.RequestAsync(request, _options.ApprovalTimeout, ct);
        try { _notifier.NotifyApprovalRequested(request); }
        catch (Exception ex) { _logger.LogWarning(ex, "Benachrichtigung für Freigabe {Id} fehlgeschlagen.", request.ApprovalId); }
        return await pending.ConfigureAwait(false);
    }

    /// <summary>Lesbare Zusammenfassung eines Befehls (für Freigabe-Benachrichtigung).</summary>
    public static string Describe(ICommand command) => command switch
    {
        PlaceOrderCommand p => F($"{(p.Side == OrderSide.Buy ? "Kauf" : "Verkauf")} {(p.Quantity is { } q ? F($"{q:0.########}") : F($"für {p.Notional:N2} €"))} {p.Symbol} {p.Type}")
            + (p.LimitPrice is { } l ? F($" Limit {l:N2}") : "") + (p.StopPrice is { } s ? F($" Stop {s:N2}") : "") + $" ({p.TimeInForce})",
        ModifyOrderCommand m => F($"Order {m.OrderId} ändern") + (m.NewQuantity is { } q ? F($", Menge {q:0.########}") : "")
            + (m.NewLimitPrice is { } l ? F($", Limit {l:N2}") : "") + (m.NewStopPrice is { } s ? F($", Stop {s:N2}") : ""),
        CancelOrderCommand c => $"Order {c.OrderId} stornieren",
        CancelAllOrdersCommand a => a.Symbol is null ? "Alle eigenen Orders stornieren" : $"Alle eigenen Orders für {a.Symbol} stornieren",
        UpdateSettingsCommand => "Einstellungen ändern",
        EngageKillSwitchCommand k => $"Not-Aus: {k.Reason}",
        _ => command.GetType().Name,
    };

    // ------------------------------------------------------------------ Ausführung

    private async Task<CommandResult> ExecuteAsync(ICommand command, ExecutionContext ctx, CancellationToken ct)
    {
        var id = ctx.Identity!;
        var acc = ctx.AccountId;
        switch (command)
        {
            case PlaceOrderCommand p:
            {
                var clientOrderId = id.ClientId == ClientRegistry.UiClientId ? p.IdempotencyKey : $"{id.ClientId}:{p.IdempotencyKey}";
                var request = new PlaceOrderRequest
                {
                    AccountId = acc, Source = p.Source, ClientName = id.Name, ClientOrderId = clientOrderId,
                    Symbol = p.Symbol.Trim(), Side = p.Side, Type = p.Type, Quantity = p.Quantity, Notional = p.Notional,
                    LimitPrice = p.LimitPrice, StopPrice = p.StopPrice, TrailAmount = p.TrailAmount, TrailPercent = p.TrailPercent,
                    TimeInForce = p.TimeInForce, ExpiresUtc = p.ExpiresUtc, StrategyTag = p.StrategyTag,
                    TakeProfitPrice = p.TakeProfitPrice, StopLossPrice = p.StopLossPrice, OcoGroupId = p.OcoGroupId,
                };
                if (p.DryRun)
                {
                    var preview = _engine.Preview(request);
                    return new CommandResult
                    {
                        Status = CommandStatus.Previewed, Data = preview,
                        Message = preview.WouldBeAccepted ? "Vorschau: Order würde angenommen." : $"Vorschau: Order würde abgelehnt – {preview.RejectReason}",
                    };
                }
                if (_engine.GetOrderByClientOrderId(acc, clientOrderId) is { } existing)
                    return new CommandResult { Status = CommandStatus.Duplicate, Data = existing, Message = "Order mit gleichem IdempotencyKey existiert bereits." };
                return MapEngine(_engine.Execute(request), acc);
            }
            case ModifyOrderCommand m:
                return MapEngine(_engine.Execute(new ModifyOrderRequest
                {
                    AccountId = acc, Source = m.Source, ClientName = id.Name, OrderId = m.OrderId,
                    NewQuantity = m.NewQuantity, NewLimitPrice = m.NewLimitPrice, NewStopPrice = m.NewStopPrice,
                }), acc);
            case CancelOrderCommand c:
                return MapEngine(_engine.Execute(new CancelOrderRequest
                {
                    AccountId = acc, Source = c.Source, ClientName = id.Name, OrderId = c.OrderId, Reason = CancelReason(c.Source, id),
                }), acc);
            case CancelAllOrdersCommand all:
            {
                var targets = SafeOrders(acc, openOnly: true)
                    .Where(o => all.Symbol is null || string.Equals(o.Symbol, all.Symbol, StringComparison.OrdinalIgnoreCase))
                    .Where(o => id.ClientId == ClientRegistry.UiClientId || IsOwnAiOrder(o, id))
                    .ToList();
                var cancelled = new List<string>();
                var failed = new List<string>();
                foreach (var o in targets)
                {
                    var r = _engine.Execute(new CancelOrderRequest { AccountId = acc, Source = all.Source, ClientName = id.Name, OrderId = o.OrderId, Reason = CancelReason(all.Source, id) });
                    (r.Accepted ? cancelled : failed).Add(o.OrderId);
                }
                var msg = $"{cancelled.Count} Order(s) storniert" + (failed.Count > 0 ? $", {failed.Count} nicht storniert" : "") + ".";
                return CommandResult.Ok(cancelled, msg);
            }
            case CreateWatchlistCommand w:
            {
                var wl = new Watchlist
                {
                    WatchlistId = _ids.NewId("wl"), Name = w.Name.Trim(), CreatedUtc = _clock.UtcNow,
                    Symbols = w.Symbols.Select(NormalizeSymbol).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).ToList(),
                };
                await _watchlists.SaveAsync(wl, ct).ConfigureAwait(false);
                _changes.RaiseWatchlistsChanged();
                return CommandResult.Ok(wl, $"Watchlist '{wl.Name}' angelegt.");
            }
            case AddToWatchlistCommand a:
                return await ChangeWatchlistAsync(a.WatchlistId, wl =>
                {
                    var s = NormalizeSymbol(a.Symbol);
                    if (s.Length == 0) return (null, "Symbol fehlt.");
                    return wl.Symbols.Contains(s) ? (wl, $"{s} ist bereits enthalten.") : (wl with { Symbols = [.. wl.Symbols, s] }, $"{s} hinzugefügt.");
                }, ct).ConfigureAwait(false);
            case RemoveFromWatchlistCommand r:
                return await ChangeWatchlistAsync(r.WatchlistId, wl =>
                {
                    var s = NormalizeSymbol(r.Symbol);
                    return wl.Symbols.Contains(s)
                        ? (wl with { Symbols = wl.Symbols.Where(x => x != s).ToList() }, $"{s} entfernt.")
                        : (null, $"{s} ist nicht in der Watchlist.");
                }, ct).ConfigureAwait(false);
            case DeleteWatchlistCommand d:
            {
                var all = await _watchlists.GetAllAsync(ct).ConfigureAwait(false);
                if (all.All(x => x.WatchlistId != d.WatchlistId)) return Rejected($"Watchlist {d.WatchlistId} nicht gefunden.");
                await _watchlists.DeleteAsync(d.WatchlistId, ct).ConfigureAwait(false);
                _changes.RaiseWatchlistsChanged();
                return CommandResult.Ok(d.WatchlistId, "Watchlist gelöscht.");
            }
            case CreateAlertCommand a:
            {
                var alert = new PriceAlert
                {
                    AlertId = _ids.NewId("alr"), Symbol = NormalizeSymbol(a.Symbol), Condition = a.Condition,
                    Threshold = a.Threshold, Note = a.Note, CreatedUtc = _clock.UtcNow,
                };
                await _alerts.SaveAsync(alert, ct).ConfigureAwait(false);
                _changes.RaiseAlertsChanged();
                return CommandResult.Ok(alert, "Alarm angelegt.");
            }
            case DeleteAlertCommand d:
            {
                var all = await _alerts.GetAllAsync(ct).ConfigureAwait(false);
                if (all.All(x => x.AlertId != d.AlertId)) return Rejected($"Alarm {d.AlertId} nicht gefunden.");
                await _alerts.DeleteAsync(d.AlertId, ct).ConfigureAwait(false);
                _changes.RaiseAlertsChanged();
                return CommandResult.Ok(d.AlertId, "Alarm gelöscht.");
            }
            case UpdateSettingsCommand u:
                return await UpdateSettingsAsync(u, ctx, ct).ConfigureAwait(false);
            case UpdateRiskLimitsCommand r:
            {
                if (_engine.AccountExists(acc))
                {
                    var er = _engine.Execute(new UpdateRiskConfigRequest { AccountId = acc, Source = r.Source, ClientName = id.Name, Risk = r.Risk });
                    if (!er.Accepted) return MapEngine(er, acc);
                }
                await _settings.UpdateAsync(s => s with { Risk = r.Risk }, ct).ConfigureAwait(false);
                return CommandResult.Ok(r.Risk, "Risiko-Limits geändert.");
            }
            case SetClientModeCommand s:
            {
                if (s.TargetClientId == ClientRegistry.UiClientId) return Rejected("Die Oberfläche hat keinen einstellbaren Modus.");
                var ok = await _clients.SetModeAsync(s.TargetClientId, s.Mode, s.Scopes, ct).ConfigureAwait(false);
                return ok ? CommandResult.Ok(_clients.Get(s.TargetClientId), $"Modus von '{s.TargetClientId}' ist jetzt {s.Mode}.")
                          : Rejected($"KI-Client {s.TargetClientId} nicht gefunden.");
            }
            case EngageKillSwitchCommand k:
            {
                var r = _engine.Execute(new EngageKillSwitchRequest { AccountId = acc, Source = k.Source, ClientName = id.Name, Reason = k.Reason });
                if (r.Accepted)
                {
                    DenyPendingApprovals("Not-Aus");
                    SafeNotify("Not-Aus ausgelöst", $"{id.Name}: {k.Reason}");
                }
                return MapEngine(r, acc);
            }
            case ReleaseKillSwitchCommand:
                return MapEngine(_engine.Execute(new ReleaseKillSwitchRequest { AccountId = acc, Source = CommandSource.Human, ClientName = id.Name, ReleasedBy = id.Name }), acc);
            case DepositCashCommand d:
                return MapEngine(_engine.Execute(new DepositCashRequest { AccountId = acc, Source = CommandSource.Human, ClientName = id.Name, Amount = d.Amount, Note = d.Note }), acc);
            case ResolveApprovalCommand a:
                return _approvals.Resolve(a.ApprovalId, a.Approved, id.Name)
                    ? CommandResult.Ok(a.ApprovalId, a.Approved ? "Freigabe erteilt." : "Freigabe abgelehnt.")
                    : Rejected($"Freigabe {a.ApprovalId} nicht gefunden oder bereits entschieden.");
            default:
                return Rejected($"Unbekannter Befehl {command.GetType().Name}.");
        }
    }

    private async Task<CommandResult> UpdateSettingsAsync(UpdateSettingsCommand u, ExecutionContext ctx, CancellationToken ct)
    {
        var current = _settings.Current;
        var next = u.Settings;
        var riskChanged = Json(current.Risk) != Json(next.Risk);
        if (u.Source == CommandSource.Ai)
        {
            if (riskChanged) return Denied("KI-Clients dürfen Risiko-Limits nicht ändern (nur in der Oberfläche).");
            if (Json(current.AiClients) != Json(next.AiClients)) return Denied("KI-Clients dürfen Modus, Rechte und KI-Clients nicht ändern (nur in der Oberfläche).");
        }
        var cryptoFeesChanged = Json(current.CryptoFees) != Json(next.CryptoFees);
        var stockFeesChanged = Json(current.StockFees) != Json(next.StockFees);
        var saved = await _settings.SaveAsync(next, ct).ConfigureAwait(false);

        var acc = ctx.AccountId;
        if (_engine.AccountExists(acc))
        {
            var name = ctx.Identity!.Name;
            if (riskChanged) _engine.Execute(new UpdateRiskConfigRequest { AccountId = acc, Source = u.Source, ClientName = name, Risk = next.Risk });
            if (cryptoFeesChanged) _engine.Execute(new UpdateFeeModelRequest { AccountId = acc, Source = u.Source, ClientName = name, AssetClass = AssetClass.Crypto, Fees = next.CryptoFees });
            if (stockFeesChanged) _engine.Execute(new UpdateFeeModelRequest { AccountId = acc, Source = u.Source, ClientName = name, AssetClass = AssetClass.Stock, Fees = next.StockFees });
        }
        return CommandResult.Ok(saved, "Einstellungen gespeichert.");
    }

    private async Task<CommandResult> ChangeWatchlistAsync(string watchlistId, Func<Watchlist, (Watchlist? Updated, string Message)> change, CancellationToken ct)
    {
        await _watchlistGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wl = (await _watchlists.GetAllAsync(ct).ConfigureAwait(false)).FirstOrDefault(x => x.WatchlistId == watchlistId);
            if (wl is null) return Rejected($"Watchlist {watchlistId} nicht gefunden.");
            var (updated, message) = change(wl);
            if (updated is null) return Rejected(message);
            if (!ReferenceEquals(updated, wl))
            {
                await _watchlists.SaveAsync(updated, ct).ConfigureAwait(false);
                _changes.RaiseWatchlistsChanged();
            }
            return CommandResult.Ok(updated, message);
        }
        finally
        {
            _watchlistGate.Release();
        }
    }

    private CommandResult MapEngine(EngineResult r, string accountId)
    {
        if (!r.Accepted)
            return Rejected((r.RejectReason ?? "Von der Engine abgelehnt.") + (r.RiskRule is null ? "" : $" [{r.RiskRule}]"));
        object? data = r;
        if (r.OrderId is not null)
        {
            try { data = _engine.GetOrder(accountId, r.OrderId) ?? (object)r; }
            catch (Exception ex) { _logger.LogDebug(ex, "Order {Id} nicht lesbar.", r.OrderId); }
        }
        return CommandResult.Ok(data);
    }

    // ------------------------------------------------------------------ Tagesverlust / Not-Aus

    private void OnEngineEvents(object? sender, EngineEventsEventArgs e)
    {
        foreach (var ev in e.Events)
        {
            switch (ev)
            {
                case DailyLossLimitHit hit:
                    var accountId = hit.AccountId;
                    SafeNotify("Tagesverlustgrenze erreicht", "KI-Clients sind bis zum Tageswechsel nur lesend; offene KI-Orders werden storniert.");
                    _ = Task.Run(() => EnforceDailyLossLimitAsync(accountId, CancellationToken.None));
                    break;
                case KillSwitchEngaged:
                    DenyPendingApprovals("Not-Aus");
                    break;
            }
        }
    }

    /// <summary>Storniert alle offenen KI-Orders, wenn die Tagesverlustgrenze erreicht ist. Liefert die Anzahl.</summary>
    public async Task<int> EnforceDailyLossLimitAsync(string accountId, CancellationToken ct = default)
    {
        await _dailyLossGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sw = Stopwatch.StartNew();
            var open = SafeOrders(accountId, openOnly: true).Where(o => o.Source == CommandSource.Ai).ToList();
            if (open.Count == 0) return 0;
            var cancelled = new List<string>();
            foreach (var o in open)
            {
                try
                {
                    var r = _engine.Execute(new CancelOrderRequest { AccountId = accountId, Source = CommandSource.Human, ClientName = "System", OrderId = o.OrderId, Reason = "daily_loss_limit" });
                    if (r.Accepted) cancelled.Add(o.OrderId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Stornierung von KI-Order {Id} nach Tagesverlust fehlgeschlagen.", o.OrderId);
                }
            }
            await AppendAuditSafeAsync(new AuditEntry
            {
                AuditId = _ids.NewId("aud"), TimestampUtc = _clock.UtcNow, Source = CommandSource.Human, ClientId = SystemClientId,
                Action = "DailyLossLimitEnforcement", ParametersJson = JsonSerializer.Serialize(new { accountId, orders = cancelled }, JsonDefaults.Options),
                Result = CommandStatus.Executed.ToString(), Message = $"Tagesverlustgrenze: {cancelled.Count} KI-Order(s) storniert.", Duration = sw.Elapsed,
            }).ConfigureAwait(false);
            return cancelled.Count;
        }
        finally
        {
            _dailyLossGate.Release();
        }
    }

    private void DenyPendingApprovals(string reason)
    {
        try
        {
            if (_approvals is ApprovalQueue q) q.ResolveAll(false, reason);
            else foreach (var p in _approvals.Pending) _approvals.Resolve(p.ApprovalId, false, reason);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Offene Freigaben konnten nicht abgelehnt werden.");
        }
    }

    // ------------------------------------------------------------------ Protokoll

    private async Task<CommandResult> AuditAsync(ICommand command, ExecutionContext ctx, CommandResult result, Stopwatch sw)
    {
        string? parameters;
        try { parameters = JsonSerializer.Serialize(command, command.GetType(), JsonDefaults.Options); }
        catch (Exception ex) { parameters = JsonSerializer.Serialize(new { error = ex.Message }); }
        var entry = new AuditEntry
        {
            AuditId = _ids.NewId("aud"),
            TimestampUtc = _clock.UtcNow,
            Source = command.Source,
            ClientId = command.ClientId ?? "",
            Action = command.GetType().Name,
            ParametersJson = parameters,
            Result = result.Status.ToString(),
            Message = result.Message,
            ApprovedBy = ctx.ApprovedBy,
            IdempotencyKey = command.IdempotencyKey,
            Duration = sw.Elapsed,
        };
        var saved = await AppendAuditSafeAsync(entry).ConfigureAwait(false);
        return saved is null ? result : result with { AuditId = saved.AuditId };
    }

    private async Task<AuditEntry?> AppendAuditSafeAsync(AuditEntry entry)
    {
        try
        {
            return await _audit.AppendAsync(entry, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Protokolleintrag für {Action} konnte nicht gespeichert werden.", entry.Action);
            return null;
        }
    }

    // ------------------------------------------------------------------ Hilfen

    private bool IsDailyLossLimitHit(string accountId)
    {
        try { return _engine.AccountExists(accountId) && _engine.GetPortfolio(accountId).DailyLossLimitHit; }
        catch (Exception ex) { _logger.LogDebug(ex, "Portfolio {Account} nicht lesbar.", accountId); return false; }
    }

    private bool IsKillSwitchOn(string accountId)
    {
        try { return _engine.AccountExists(accountId) && _engine.IsKillSwitchEngaged(accountId); }
        catch { return false; }
    }

    private RiskConfig GetRisk(string accountId)
    {
        try { if (_engine.AccountExists(accountId)) return _engine.GetRiskConfig(accountId); }
        catch (Exception ex) { _logger.LogDebug(ex, "Risiko-Konfiguration {Account} nicht lesbar.", accountId); }
        return _settings.Current.Risk;
    }

    private IReadOnlyList<Order> SafeOrders(string accountId, bool openOnly)
    {
        try { return _engine.AccountExists(accountId) ? _engine.GetOrders(accountId, openOnly) : []; }
        catch (Exception ex) { _logger.LogDebug(ex, "Orders {Account} nicht lesbar.", accountId); return []; }
    }

    private void SafeNotify(string title, string message)
    {
        try { _notifier.Notify(title, message); }
        catch (Exception ex) { _logger.LogWarning(ex, "Benachrichtigung fehlgeschlagen."); }
    }

    private static string CancelReason(CommandSource source, ClientIdentity id) => source == CommandSource.Ai ? $"ai:{id.Name}" : "user";

    private static string NormalizeSymbol(string symbol) => (symbol ?? "").Trim().ToUpperInvariant();

    private static string F(FormattableString s) => s.ToString(De);

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonDefaults.Options);

    private static CommandResult Denied(string message) => CommandResult.Fail(CommandStatus.Denied, message);
    private static CommandResult Rejected(string message) => CommandResult.Fail(CommandStatus.Rejected, message);
}
