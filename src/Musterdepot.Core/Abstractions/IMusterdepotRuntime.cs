namespace Musterdepot.Core.Abstractions;

/// <summary>
/// Verbindet Marktdaten → Engine → Persistence und treibt die Uhr. Von der App (oder Tests) gestartet.
/// Betrieb im Hintergrund: Standby → <see cref="SuspendAsync"/>, Aufwachen → <see cref="ResumeAsync"/>.
/// </summary>
public interface IMusterdepotRuntime : IAsyncDisposable
{
    bool IsRunning { get; }
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task SuspendAsync(CancellationToken ct = default);
    Task ResumeAsync(CancellationToken ct = default);
}
