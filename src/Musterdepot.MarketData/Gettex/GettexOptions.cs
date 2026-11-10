using System.Globalization;
using System.Text.RegularExpressions;

namespace Musterdepot.MarketData.Gettex;

/// <summary>
/// Einstellungen für den gettex-Abruf. Das Dateiformat ist nicht offiziell dokumentiert – Basis-URL und
/// Dateinamensschema sind daher frei konfigurierbar.
/// </summary>
public sealed record GettexOptions
{
    /// <summary>Annahme: Post-Trade-Dateien liegen unter dieser URL (änderbar).</summary>
    public string BaseUrl { get; init; } = "https://www.gettex.de/handel/delayed-data/posttrade-data/";

    /// <summary>
    /// Dateinamensschemata je 15-Minuten-Zeitfenster. Platzhalter in geschweiften Klammern sind .NET-Datumsformate
    /// des Fensterbeginns, z. B. <c>{yyyyMMdd}</c>, <c>{HH}</c>, <c>{mm}</c>. Annahme: zwei Segmente (munc, mund), gzip.
    /// </summary>
    public IReadOnlyList<string> FileNamePatterns { get; init; } =
    [
        "posttrade.{yyyyMMdd}.{HH}.{mm}.munc.csv.gz",
        "posttrade.{yyyyMMdd}.{HH}.{mm}.mund.csv.gz",
    ];

    /// <summary>Takt neuer Dateien.</summary>
    public TimeSpan FileInterval { get; init; } = TimeSpan.FromMinutes(15);
    /// <summary>Wie oft abgefragt wird.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMinutes(15);
    /// <summary>Dateien der letzten 24 h.</summary>
    public TimeSpan Lookback { get; init; } = TimeSpan.FromHours(24);
    /// <summary>Eine Datei erscheint erst einige Minuten nach Ende ihres Fensters.</summary>
    public TimeSpan PublicationDelay { get; init; } = TimeSpan.FromMinutes(1);
    /// <summary>Zeitfenster in Dateinamen sind Berliner Ortszeit (sonst UTC).</summary>
    public bool FileTimesAreLocal { get; init; } = true;
    /// <summary>Verzögerung der Daten.</summary>
    public int DelayedMinutes { get; init; } = 15;
    /// <summary>Optional: statt HTTP aus diesem Ordner lesen (gleiches Namensschema).</summary>
    public string? LocalDirectory { get; init; }
    /// <summary>Höchstzahl Dateiabrufe je Abfragezyklus (beim ersten Start 96 Fenster × Segmente).</summary>
    public int MaxFilesPerPoll { get; init; } = 250;
    public GettexParserOptions Parser { get; init; } = new();

    /// <summary>Setzt den Dateinamen für einen Fensterbeginn zusammen.</summary>
    public static string FormatFileName(string pattern, DateTime slotStart)
        => Regex.Replace(pattern, "\\{([^{}]+)\\}", m => slotStart.ToString(m.Groups[1].Value, CultureInfo.InvariantCulture));
}

public sealed record GettexParserOptions
{
    /// <summary>Zeiten ohne Zonenangabe gelten als UTC statt Europe/Berlin.</summary>
    public bool AssumeUtcWhenNoOffset { get; init; }
    /// <summary>
    /// Feste Spaltenzuordnung für Dateien ohne Kopfzeile, z. B. ["isin","date","time","currency","price","volume"].
    /// Erlaubte Rollen: isin, datetime, date, time, price, volume, bid, ask, bidsize, asksize, currency, ignore.
    /// Leer = Spalten heuristisch erkennen.
    /// </summary>
    public IReadOnlyList<string> HeaderlessColumns { get; init; } = [];
    /// <summary>Höchstzahl gesammelter Warnungen je Datei.</summary>
    public int MaxWarnings { get; init; } = 20;
}
