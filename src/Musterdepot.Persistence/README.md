# Musterdepot.Persistence

Eine SQLite-Datei (Standard `AppPaths.DatabasePath`, `%LOCALAPPDATA%\Musterdepot\musterdepot.db`), EF Core 10, WAL-Modus.
Registrierung: `services.AddMusterdepotPersistence(databasePath)` (alles Singletons).

## Speicherformat
- Geldbeträge/Mengen (`decimal`) als TEXT, invariant und verlustfrei (`DecimalToInvariantStringConverter`, Skalierung bleibt: `1.10`).
- Zeitpunkte als TEXT in UTC `yyyy-MM-ddTHH:mm:ss.fffffffZ` (lexikografisch = zeitlich sortierbar).
- Enums als TEXT; Events, Einstellungen, Risiko/Gebühren und FIFO-Lots als JSON über `JsonDefaults`.

## Tabellen
| Tabelle | Inhalt |
|---|---|
| SchemaInfo | eine Zeile (Id = 1): Schema-Version, Anlage-/Änderungszeit |
| Events | Event-Log (Quelle der Wahrheit): Id (Einfügereihenfolge), AccountId, Sequence, Type, SchemaVersion, TimestampUtc, EventId, Json |
| Accounts, Orders, Fills, Positions, CashEntries | Projektionen, beim Anhängen von Events in derselben Transaktion aktualisiert (Export/Analyse) |
| Candles | Schlüssel Symbol + Timeframe + OpenTimeUtc; Prune löscht nur M1 älter als 90 Tage |
| AiActions | Protokoll aller Befehle/KI-Aktionen (`IAuditLog`) |
| Settings | `AppSettings` als JSON unter dem Schlüssel `AppSettings` |
| Watchlists, WatchlistItems, Alerts, Instruments | Stores |

## Schema-Version
`EnsureCreated` legt beim ersten Öffnen alle Tabellen an. `MusterdepotDatabase.CurrentSchemaVersion` (derzeit 1) steht in `SchemaInfo`.
Beim Öffnen: Zeile fehlt → anlegen; ältere Version → Schritte in `MusterdepotDatabase.Migrate` (je Version ein SQL-Schritt,
z. B. `ALTER TABLE … ADD COLUMN …`) und Version hochsetzen; neuere Version → Warnung im Log.
Bei einer Schemaänderung: Version erhöhen und Migrationsschritt ergänzen (neue Spalten nullable).

## Nebenläufigkeit
Je Vorgang ein eigener `DbContext` (`IDbContextFactory<MusterdepotDbContext>`), Schreibvorgänge über eine gemeinsame Schreib-Semaphore.
