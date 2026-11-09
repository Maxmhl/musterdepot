using System.Collections.Concurrent;
using Musterdepot.Core.Abstractions;

namespace Musterdepot.Commands;

/// <summary>
/// Freigabe-Warteschlange (Modus confirm). <see cref="RequestAsync"/> wartet auf <see cref="Resolve"/> oder
/// Timeout (→ <see cref="ApprovalState.Expired"/>). Ereignisse kommen auf dem auslösenden Thread.
/// </summary>
public sealed class ApprovalQueue(IClock clock) : IApprovalService
{
    private sealed record Entry(ApprovalRequest Request, TaskCompletionSource<ApprovalRequest> Completion);

    private readonly ConcurrentDictionary<string, Entry> _pending = new();

    public event EventHandler<ApprovalRequest>? RequestAdded;
    public event EventHandler<ApprovalRequest>? RequestResolved;

    public IReadOnlyList<ApprovalRequest> Pending =>
        _pending.Values.Select(e => e.Request).OrderBy(r => r.CreatedUtc).ThenBy(r => r.ApprovalId, StringComparer.Ordinal).ToList();

    public async Task<ApprovalRequest> RequestAsync(ApprovalRequest request, TimeSpan timeout, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = request with { State = ApprovalState.Pending, DecidedBy = null, DecidedUtc = null };
        var entry = new Entry(request, new TaskCompletionSource<ApprovalRequest>(TaskCreationOptions.RunContinuationsAsynchronously));
        if (!_pending.TryAdd(request.ApprovalId, entry))
            throw new InvalidOperationException($"Freigabe {request.ApprovalId} existiert bereits.");
        RequestAdded?.Invoke(this, request);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout != Timeout.InfiniteTimeSpan) timeoutCts.CancelAfter(timeout);
        try
        {
            return await entry.Completion.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var by = ct.IsCancellationRequested ? "abgebrochen" : "Zeitüberschreitung";
            if (Complete(request.ApprovalId, ApprovalState.Expired, by) is { } expired) return expired;
            // In der Zwischenzeit entschieden.
            return await entry.Completion.Task.ConfigureAwait(false);
        }
    }

    public bool Resolve(string approvalId, bool approved, string decidedBy)
        => Complete(approvalId, approved ? ApprovalState.Approved : ApprovalState.Denied, decidedBy) is not null;

    /// <summary>Alle offenen Freigaben entscheiden (z. B. ablehnen bei Not-Aus). Liefert die Anzahl.</summary>
    public int ResolveAll(bool approved, string decidedBy)
        => _pending.Keys.ToList().Count(id => Resolve(id, approved, decidedBy));

    private ApprovalRequest? Complete(string approvalId, ApprovalState state, string decidedBy)
    {
        if (!_pending.TryRemove(approvalId, out var entry)) return null;
        var decided = entry.Request with { State = state, DecidedBy = decidedBy, DecidedUtc = clock.UtcNow };
        entry.Completion.TrySetResult(decided);
        RequestResolved?.Invoke(this, decided);
        return decided;
    }
}
