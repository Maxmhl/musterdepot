using Musterdepot.Core.Abstractions;
using Musterdepot.Core.Domain;

namespace Musterdepot.Commands;

/// <summary>Einstellungen der Befehlsschicht und der Laufzeit.</summary>
public sealed class CommandOptions
{
    /// <summary>Wie lange eine Freigabe (Modus confirm) auf Erlauben/Ablehnen wartet, danach Expired → Denied.</summary>
    public TimeSpan ApprovalTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Im Modus confirm brauchen Schreibbefehle mit diesen Berechtigungen eine Freigabe
    /// (Standard: Orders und Einstellungen; Watchlists/Alarme nicht). Not-Aus und dry_run nie.</summary>
    public ClientScopes ConfirmRequiredScopes { get; set; } = ClientScopes.Trade | ClientScopes.Settings;

    /// <summary>Wie lange Ergebnisse für die Idempotenz (ClientId + IdempotencyKey) im Speicher bleiben.</summary>
    public TimeSpan IdempotencyRetention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Takt für <see cref="ITradingEngine.OnClockTick"/>.</summary>
    public TimeSpan ClockTickInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Drosselung der Kurs-Lesemodelle (250 ms = max. 4 Updates/s).</summary>
    public TimeSpan QuoteThrottleInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Aufbewahrung der 1-Minuten-Kerzen (Prune einmal täglich).</summary>
    public TimeSpan MinuteCandleRetention { get; set; } = TimeSpan.FromDays(90);

    public int DefaultCandleLimit { get; set; } = 500;
    public int MaxCandleLimit { get; set; } = 5000;
    public int MaxOrderBookDepth { get; set; } = 1000;

    /// <summary>Anzahl Protokolleinträge im Lesemodell.</summary>
    public int AuditReadModelSize { get; set; } = 500;

    /// <summary>Kontoname für das automatisch angelegte Standardkonto.</summary>
    public string DefaultAccountName { get; set; } = "Musterdepot";
}

/// <summary>Standard-Benachrichtigung: tut nichts (die Oberfläche registriert eine eigene).</summary>
public sealed class NullUserNotifier : IUserNotifier
{
    public static NullUserNotifier Instance { get; } = new();
    public void Notify(string title, string message) { }
    public void NotifyApprovalRequested(ApprovalRequest request) { }
}
