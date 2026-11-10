namespace Musterdepot.Engine;

/// <summary>
/// Zusätzliche Engine-Einstellungen, die (noch) nicht in <see cref="Musterdepot.Core.Domain.ExecutionConfig"/> stehen.
/// </summary>
public sealed record EngineOptions
{
    /// <summary>Equity-Kurve: höchstens ein Bewertungspunkt je Intervall bei reinen Kursänderungen (Fills immer).</summary>
    public TimeSpan EquitySnapshotInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Zusätzliche Toleranz für verzögerte Daten (gettex liefert alle 15 Min eine neue Datei): Ein verzögerter Kurs gilt als
    /// frisch, solange <c>jetzt − AsOf − DelayedMinutes ≤ MaxQuoteAge + DelayedDataTolerance</c>.
    /// </summary>
    public TimeSpan DelayedDataTolerance { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Max. Fills, die ein einzelner Orderbuch-Durchlauf erzeugt (Schutz gegen sehr tiefe Bücher).</summary>
    public int MaxBookLevelsPerSweep { get; init; } = 1000;

    public static EngineOptions Default { get; } = new();
}

/// <summary>Rundungsregeln für Geld (Basiswährung, Cent-genau, kaufmännisch).</summary>
public static class Money
{
    public static decimal Round(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    /// <summary>Menge auf Mengenschritt abrunden.</summary>
    public static decimal FloorToStep(decimal quantity, decimal step)
    {
        if (step <= 0) return quantity;
        return Math.Floor(quantity / step) * step;
    }

    public static bool IsMultipleOf(decimal quantity, decimal step) => step <= 0 || quantity % step == 0;

    /// <summary>Preis auf Tick runden (aufwärts oder abwärts).</summary>
    public static decimal RoundToTick(decimal price, decimal tick, bool up)
    {
        if (tick <= 0) return price;
        var n = price / tick;
        return (up ? Math.Ceiling(n) : Math.Floor(n)) * tick;
    }

    /// <summary>Entfernt überflüssige Nachkommanullen (nur Darstellung, Wert unverändert).</summary>
    public static decimal Normalize(decimal value) => value / 1.0000000000000000000000000000m;
}
