using Musterdepot.Core.Domain;
using Musterdepot.Core.MarketData;
using Musterdepot.Recorder.Recording;

namespace Musterdepot.Recorder.Fixtures;

/// <summary>Strukturprüfung eines Orderskripts (optional gegen die zugehörige Aufzeichnung).</summary>
public static class OrderScriptValidator
{
    public static IReadOnlyList<string> Validate(OrderScript script, IReadOnlyList<MarketEvent>? replay = null)
    {
        var errors = new List<string>();
        if (script.FormatVersion != OrderScript.CurrentFormatVersion)
            errors.Add($"formatVersion {script.FormatVersion} wird nicht unterstützt (erwartet {OrderScript.CurrentFormatVersion}).");
        if (string.IsNullOrWhiteSpace(script.Scenario)) errors.Add("scenario fehlt.");
        if (string.IsNullOrWhiteSpace(script.Replay)) errors.Add("replay fehlt.");
        if (script.Accounts.Count == 0) errors.Add("Mindestens ein Konto (accounts) nötig.");

        var accounts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in script.Accounts)
        {
            if (!accounts.Add(a.AccountId)) errors.Add($"Konto '{a.AccountId}' doppelt.");
            if (a.InitialCash < 0) errors.Add($"Konto '{a.AccountId}': initialCash negativ.");
        }

        var instruments = new HashSet<string>(StringComparer.Ordinal);
        foreach (var i in script.Instruments)
            if (!instruments.Add(i.Symbol)) errors.Add($"Instrument '{i.Symbol}' doppelt.");

        HashSet<string>? replaySymbols = null;
        DateTimeOffset? first = null, last = null;
        if (replay is { Count: > 0 })
        {
            replaySymbols = new HashSet<string>(replay.Select(e => e.Symbol), StringComparer.Ordinal);
            first = replay.Min(ReplayClock.TimeOf);
            last = replay.Max(ReplayClock.TimeOf);
            foreach (var s in replaySymbols.Where(s => s != "*" && !instruments.Contains(s)))
                errors.Add($"Symbol '{s}' kommt in der Aufzeichnung vor, fehlt aber unter instruments.");
        }

        var placed = new HashSet<string>(StringComparer.Ordinal);
        DateTimeOffset? previous = null;
        for (int n = 0; n < script.Steps.Count; n++)
        {
            var step = script.Steps[n];
            var where = $"Schritt {n + 1} ({step.AtUtc:O}, {step.Action})";
            if (previous is { } p && step.AtUtc < p) errors.Add($"{where}: Schritte sind nicht zeitlich sortiert.");
            previous = step.AtUtc;
            if (step.AtUtc.Offset != TimeSpan.Zero) errors.Add($"{where}: atUtc muss UTC sein.");
            if (first is { } f && (step.AtUtc < f || step.AtUtc > last)) errors.Add($"{where}: liegt außerhalb der Aufzeichnung ({f:O} – {last:O}).");
            if (!accounts.Contains(step.AccountId)) errors.Add($"{where}: unbekanntes Konto '{step.AccountId}'.");

            int payloads = (step.PlaceOrder is null ? 0 : 1) + (step.Cancel is null ? 0 : 1) + (step.Modify is null ? 0 : 1);
            if (payloads != 1) errors.Add($"{where}: genau eines von placeOrder/cancel/modify angeben.");

            switch (step.Action)
            {
                case OrderScriptAction.PlaceOrder when step.PlaceOrder is { } o:
                    ValidatePlace(o, where, errors);
                    if (!placed.Add(o.ClientOrderId)) errors.Add($"{where}: clientOrderId '{o.ClientOrderId}' doppelt.");
                    if (instruments.Count > 0 && !instruments.Contains(o.Symbol)) errors.Add($"{where}: Symbol '{o.Symbol}' fehlt unter instruments.");
                    if (replaySymbols is not null && !replaySymbols.Contains(o.Symbol)) errors.Add($"{where}: Symbol '{o.Symbol}' kommt in der Aufzeichnung nicht vor.");
                    break;
                case OrderScriptAction.Cancel when step.Cancel is { } c:
                    ValidateTarget(c.ClientOrderId, c.OrderId, placed, where, errors);
                    break;
                case OrderScriptAction.Modify when step.Modify is { } m:
                    ValidateTarget(m.ClientOrderId, m.OrderId, placed, where, errors);
                    if (m.NewQuantity is null && m.NewLimitPrice is null && m.NewStopPrice is null)
                        errors.Add($"{where}: modify ohne Änderung.");
                    if (m.NewQuantity is <= 0) errors.Add($"{where}: newQuantity muss > 0 sein.");
                    break;
                default:
                    errors.Add($"{where}: Feld für Aktion {step.Action} fehlt.");
                    break;
            }

            if (step.Expect is { MinFillPrice: { } min, MaxFillPrice: { } max } && min > max)
                errors.Add($"{where}: expect.minFillPrice > expect.maxFillPrice.");
        }
        return errors;
    }

    private static void ValidatePlace(OrderScriptPlaceOrder o, string where, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(o.ClientOrderId)) errors.Add($"{where}: clientOrderId fehlt.");
        if (string.IsNullOrWhiteSpace(o.Symbol)) errors.Add($"{where}: symbol fehlt.");
        if ((o.Quantity is null) == (o.Notional is null)) errors.Add($"{where}: genau eines von quantity/notional angeben.");
        if (o.Quantity is <= 0) errors.Add($"{where}: quantity muss > 0 sein.");
        if (o.Notional is <= 0) errors.Add($"{where}: notional muss > 0 sein.");
        bool needsLimit = o.Type is OrderType.Limit or OrderType.StopLimit;
        bool needsStop = o.Type is OrderType.Stop or OrderType.StopLimit;
        if (needsLimit && o.LimitPrice is null) errors.Add($"{where}: {o.Type} braucht limitPrice.");
        if (!needsLimit && o.LimitPrice is not null) errors.Add($"{where}: {o.Type} darf keinen limitPrice haben.");
        if (needsStop && o.StopPrice is null) errors.Add($"{where}: {o.Type} braucht stopPrice.");
        if (!needsStop && o.StopPrice is not null) errors.Add($"{where}: {o.Type} darf keinen stopPrice haben.");
        if (o.Type == OrderType.TrailingStop && (o.TrailAmount is null) == (o.TrailPercent is null))
            errors.Add($"{where}: TrailingStop braucht genau eines von trailAmount/trailPercent.");
        if (o.LimitPrice is <= 0 || o.StopPrice is <= 0) errors.Add($"{where}: Preise müssen > 0 sein.");
        if (o.TimeInForce == TimeInForce.Gtd && o.ExpiresUtc is null) errors.Add($"{where}: GTD braucht expiresUtc.");
    }

    private static void ValidateTarget(string? clientOrderId, string? orderId, HashSet<string> placed, string where, List<string> errors)
    {
        if ((clientOrderId is null) == (orderId is null)) errors.Add($"{where}: genau eines von clientOrderId/orderId angeben.");
        if (clientOrderId is not null && !placed.Contains(clientOrderId))
            errors.Add($"{where}: clientOrderId '{clientOrderId}' wurde vorher nicht platziert.");
    }
}
