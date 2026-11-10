using Musterdepot.Core.Abstractions;

namespace Musterdepot.MarketData.Infrastructure;

public sealed record BackoffOptions
{
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(60);
    public double Multiplier { get; init; } = 2.0;
    /// <summary>Anteil der zufälligen Streuung (0.25 = ±25 %).</summary>
    public double JitterFactor { get; init; } = 0.25;
}

/// <summary>Exponentielles Backoff mit Jitter (Zufall von außen, damit Tests deterministisch sind).</summary>
public sealed class ExponentialBackoff(BackoffOptions options, IRandomSource random)
{
    public BackoffOptions Options { get; } = options;

    /// <summary>Wartezeit vor dem Versuch Nr. <paramref name="attempt"/> (0-basiert).</summary>
    public TimeSpan GetDelay(int attempt)
    {
        if (attempt < 0) attempt = 0;
        var maxMs = Options.MaxDelay.TotalMilliseconds;
        var baseMs = Options.InitialDelay.TotalMilliseconds * Math.Pow(Options.Multiplier, Math.Min(attempt, 40));
        if (double.IsInfinity(baseMs) || baseMs > maxMs) baseMs = maxMs;
        var jitter = 1.0 + Options.JitterFactor * (2.0 * random.NextDouble() - 1.0);
        var ms = Math.Clamp(baseMs * jitter, 0, maxMs);
        return TimeSpan.FromMilliseconds(ms);
    }
}

/// <summary>
/// Begrenzt Verbindungsversuche je Zeitfenster (gleitendes Fenster).
/// Kraken sperrt ab etwa 150 Verbindungen in 10 Minuten je IP; Standard hier 100 als Sicherheitsabstand.
/// Eine Instanz kann von mehreren Quellen desselben Hosts geteilt werden.
/// </summary>
public sealed class ConnectionRateLimiter(int maxConnections, TimeSpan window, IClock clock)
{
    private readonly Queue<DateTimeOffset> _attempts = new();
    private readonly Lock _lock = new();

    public int MaxConnections { get; } = maxConnections > 0 ? maxConnections : throw new ArgumentOutOfRangeException(nameof(maxConnections));
    public TimeSpan Window { get; } = window;

    public static ConnectionRateLimiter ForKraken(IClock clock) => new(100, TimeSpan.FromMinutes(10), clock);

    public int CountInWindow
    {
        get { lock (_lock) { Prune(clock.UtcNow); return _attempts.Count; } }
    }

    /// <summary>Versucht, einen Verbindungsversuch zu verbuchen. Bei false sagt <paramref name="retryAfter"/>, wann wieder Platz ist.</summary>
    public bool TryAcquire(out TimeSpan retryAfter)
    {
        lock (_lock)
        {
            var now = clock.UtcNow;
            Prune(now);
            if (_attempts.Count < MaxConnections)
            {
                _attempts.Enqueue(now);
                retryAfter = TimeSpan.Zero;
                return true;
            }
            retryAfter = _attempts.Peek() + Window - now;
            if (retryAfter < TimeSpan.Zero) retryAfter = TimeSpan.Zero;
            return false;
        }
    }

    private void Prune(DateTimeOffset now)
    {
        while (_attempts.Count > 0 && _attempts.Peek() <= now - Window) _attempts.Dequeue();
    }
}
