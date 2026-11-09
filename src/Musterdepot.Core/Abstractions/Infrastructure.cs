namespace Musterdepot.Core.Abstractions;

/// <summary>Uhr ist austauschbar (Live, Replay, Backtest, Tests).</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new();
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>Manuell gesteuerte Uhr für Tests und Replay.</summary>
public sealed class ManualClock(DateTimeOffset start) : IClock
{
    private DateTimeOffset _now = start;
    private readonly Lock _lock = new();
    public DateTimeOffset UtcNow { get { lock (_lock) return _now; } }
    public void Set(DateTimeOffset t) { lock (_lock) _now = t; }
    public void Advance(TimeSpan d) { lock (_lock) _now += d; }
}

/// <summary>ID-Erzeugung wird von außen gesetzt (deterministisch in Tests).</summary>
public interface IIdGenerator
{
    string NewId(string prefix);
}

public sealed class GuidIdGenerator : IIdGenerator
{
    public string NewId(string prefix) => $"{prefix}_{Guid.CreateVersion7():N}";
}

public sealed class SequentialIdGenerator : IIdGenerator
{
    private long _n;
    public string NewId(string prefix) => $"{prefix}_{Interlocked.Increment(ref _n):D6}";
}

/// <summary>Zufall wird von außen gesetzt (deterministisch in Tests).</summary>
public interface IRandomSource
{
    double NextDouble();
}

public sealed class SeededRandom(int seed) : IRandomSource
{
    private readonly Random _r = new(seed);
    private readonly Lock _lock = new();
    public double NextDouble() { lock (_lock) return _r.NextDouble(); }
}
