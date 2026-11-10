# Tests und Qualität (Workstream 5)

## Teststrategie

| Ebene | Projekt | Was | Daten/Uhr |
|---|---|---|---|
| Einheit | `Musterdepot.Engine.Tests` | Ordertypen, Fills, Gebühren, FIFO, Risiko-Gate (`Decide`/`Evolve`) | `ManualClock`, `SequentialIdGenerator`, `SeededRandom` |
| Einheit | `Musterdepot.MarketData.Tests` | Parser (Kraken, Bitvavo, gettex), Reconnect, Nachladen | aufgezeichnete Nachrichten |
| Einheit | `Musterdepot.Commands.Tests` | Prüfung, Limits, Freigabe, Protokoll, Idempotenz | Fakes |
| MCP | `Musterdepot.Mcp.Tests` | SDK-Client gegen den echten lokalen Server (127.0.0.1, Token, Origin/Host) | Fakes |
| Integration | `Musterdepot.IntegrationTests` | Golden Replays, Eigenschaftstests über DI, Oberfläche ↔ MCP, Runtime Ende-zu-Ende | `tests/data/replays`, `TestHost` |
| Werkzeug | `tools/Musterdepot.Recorder` | Aufzeichnen, Prüfen, Ausschneiden, Fixtures erzeugen | echte Feeds / Generator |

Grundsätze: Geld nur `decimal`; Uhr, IDs und Zufall kommen von außen (deterministisch); jeder Test läuft ohne Netz;
Testdaten liegen als Text im Repository und sind reproduzierbar.

Ausführen:

```bash
dotnet test tests/Musterdepot.IntegrationTests      # nur Integration
build/build.sh                                      # Linux: alles bauen + alle Tests (trx unter TestResults/)
dotnet test Musterdepot.slnx                        # Windows: alles
```

## Gerüst in `tests/Musterdepot.IntegrationTests`

| Klasse | Zweck |
|---|---|
| `Infrastructure/ReplayFixture` | lädt `<name>.orders.json` + die genannte Aufzeichnung; `Timeline()` mischt Marktereignisse und Orderschritte zeitlich (bei Gleichstand Marktdaten zuerst); `ReplayFixture.Names()` für `[MemberData]` |
| `Infrastructure/GoldenFile` | vergleicht ein Objekt (als eingerücktes JSON mit `JsonDefaults`-Regeln, LF) mit `tests/data/golden/<name>.json`; `UPDATE_GOLDEN=1` schreibt neu; bei Abweichung entsteht `<name>.actual.json` (ignoriert) und die Meldung nennt die erste abweichende Zeile |
| `Infrastructure/TestHost` | DI-Container mit allen `AddMusterdepot*`-Erweiterungen, temporärem Datenverzeichnis über `MUSTERDEPOT_DATA_DIR` (SQLite darin), `ManualClock`, `SequentialIdGenerator`, `SeededRandom`, Fake-Marktdaten (`DataSourceSettings.UseFakeData`, keine Netzquellen) oder mit `ReplayFile` eine `RecordedMarketDataSource` auf der Host-Uhr (ohne AutoPlay, Fortschritt per `host.Replay.AdvanceTo`), MCP auf Port 0 ohne Pipe, kurze `CommandOptions`; `DataDirectory` für Neustart-Tests, `ConfigureServices` zum Ersetzen einzelner Dienste. Höchstens ein Host gleichzeitig (Umgebungsvariable ist prozessweit) – immer `await using`, und Testklassen mit `[Collection(TestHostCollection.Name)]` markieren (nicht parallel, sonst Verklemmung im xUnit-Synchronisationskontext) |
| `Infrastructure/GoldenReplay` | setzt ein Orderskript in ein `BacktestScript` der Engine um, spielt es mit `BacktestRunner` ab (Golden-Format) und prüft die `expect`-Felder |
| `Infrastructure/Invariants` | Invarianten über die öffentliche `ITradingEngine`-Schnittstelle: Geld bleibt erhalten (cent-genau), keine negativen Mengen, Limit-Fills, Risiko-Gate aus dem Event-Log, Zustands-Fingerabdruck für Live ↔ wiederhergestellt |
| `Infrastructure/OrderBookState` | baut ein Orderbuch aus Snapshot/Deltas nach |
| `Infrastructure/TestPaths` | Repository-Wurzel, `tests/data/...`, Temp-Ordner |

Das Orderskript-Modell (`OrderScript`, `OrderScriptValidator`, `OrderScriptJson`), der Generator und die
`inspect`-Statistik liegen im Recorder (`Musterdepot.Recorder.Fixtures` / `.Inspection`); das Testprojekt referenziert ihn.

Tests der ersten Runde (Werkzeuge und Daten):

- `FixtureTests`: lesbar, nach Replay-Zeit sortiert, AsOf je Symbol monoton, Zeilen kanonisch, Rundlauf über `.jsonl`,
  `.jsonl.gz` und `MarketEventJsonl.Writer`; Buch nie gekreuzt, Quotes = bestes Bid/Ask, Trades zum besten Preis,
  Kerzen = Trades ihrer Minute; Szenario-Invarianten (Lücke, Kurssprung, 15 Min. Verzögerung, Viertelstunden-Dateien).
- `OrderScriptTests`: Skripte valide gegen ihre Aufzeichnung, JSON-Rundlauf, Umsetzung in `PlaceOrderRequest`/`CancelOrderRequest`
  und `PlaceOrderCommand`/`CancelOrderCommand`, Validator findet typische Fehler, `Timeline()`.
- `GeneratorTests`: eingecheckte Fixtures = Generatorausgabe mit Standard-Seed.
- `RecorderToolTests`: Argumente, Zeitangaben, Quellen, stündliche Rotation, `inspect` (auch kaputte/abgeschnittene Dateien), `slice`, CLI.
- `GoldenFileTests`: Verhalten von `GoldenFile`; Golden-Statistik je Szenario (`tests/data/golden/fixture-stats/*.json`).
- `TestHostTests`: Datenverzeichnis, Wiederherstellen der Umgebung, deterministische Infrastruktur, Überschreiben, Serialisierung.

Tests der zweiten Runde (gegen die echten Implementierungen):

- `GoldenReplayTests`: je Szenario Golden-Datei `tests/data/golden/replays/<szenario>.json`, alle `expect`-Felder,
  Determinismus, Geld/Risiko-Invarianten; Gap: Stop während der Lücke gefüllt (`ExecutedDuringGap`, `FillQuality.Gap`),
  Stop nach Kurssprung > 200 EUR unter dem Stop, stornierte Order nie gefüllt; SAP: kein Fill vor Ordereingang
  (`MarketDataAsOfUtc` > `CreatedUtc`), `FillQuality.Synthetic`, Market-Kauf erst mit der nächsten gettex-Datei gefüllt.
- `DiPropertyTests` (FsCheck, 25 Folgen + 6 feste Seeds): Befehle durch `ICommandBus` (Mensch und KI), Marktdaten in die
  Engine aus dem Container; nach jeder Aktion `Invariants.CheckAccount`; danach Risiko-Gate aus dem Event-Log
  (kein Order während Not-Aus, MaxOrderNotional, Preisband), abgelehnte Befehle hinterlassen keine Order, absichtlich
  zu große/zu weit entfernte Orders werden immer abgelehnt, KI kann den Not-Aus nie lösen, `LoadFromEvents` ≡ live.
- `RuntimeEndToEndTests`: `IMusterdepotRuntime` mit SQLite, Replay, Engine, Befehlen und MCP: Start, Orders,
  Suspend/Resume (Quelle `Suspended` → `Connected`), Stop, Neustart mit neuem `ServiceProvider` → identischer Zustand,
  Idempotenz und Sequenz überleben den Neustart; dasselbe als FsCheck-Eigenschaft mit zufälligen Orders (5 Läufe);
  Modus confirm: Freigabe über `IApprovalService.Resolve`, Ablehnung über `ResolveApprovalCommand`, KI kann nicht
  selbst freigeben, Timeout → Denied, Freigebender im Protokoll; Not-Aus: KI löst aus, KI (auch als „Mensch“ getarnt)
  kann nicht lösen, Mensch löst.
- `UiMcpParityTests`: jedes Szenario einmal über `ICommandBus` (Source=Human, Client `ui`), einmal über `place_order`/
  `cancel_order` des echten MCP-Servers (Port 0, Token aus `ITokenService.CreateClientAsync(..., Autonomous)`,
  SDK-Client mit Bearer-Header) → gleiche Status, Orders, Fills, Buchungen, Portfolio (ohne IDs, Source,
  Client-Namen, Storno-Auslöser); `get_portfolio` = Engine. Not-Aus über `engage_kill_switch`, kein Lösen-Tool,
  Mensch löst. Uhrtakt nur an festen Stellen (deterministisch).
- `RecorderFakeSourceTests`: `record --sources fake --seconds 3` schreibt eine lesbare, geordnete Aufzeichnung;
  Abbruch (wie Strg+C) beendet sauber, stündliche Rotation ist Standard.

## Testdaten: `tests/data/replays`

| Szenario | Inhalt | Ereignisse | Orderskript prüft |
|---|---|---|---|
| `btc-eur-quiet` | BTC/EUR, Kraken, 02.11.2026 10:00–10:30 UTC: Status, Buch-Snapshot alle 5 Min + Deltas je Sekunde, Trades (Aggressor, TradeId), Quotes alle 2 s (Bid/Ask/Größen/24h), 30 M1-Kerzen | ≈ 4.000 | Market (Buch), ruhendes Limit, KI-Limit mit Strategie-Tag, Notional, Stop-Limit, Storno, Preisband-Ablehnung, MaxOrderNotional-Ablehnung |
| `btc-eur-gap` | BTC/EUR, 03.11.2026 10:00–10:25: Standby 10:08 (`Suspended`), Aufwachen 10:15:20 (`Reconnecting` → `Connected`), `GapDetected` 10:08–10:15 mit 7 nachgeladenen Kerzen (`IsBackfill`, Kurs ≈ −1.800 EUR), Kurssprung −420 EUR um 10:18:30 zwischen zwei Trades | ≈ 2.350 | Stop gefüllt **während der Lücke** (`ExecutedDuringGap`), Storno vor der Lücke, Stop nach Kurssprung zum **ersten verfügbaren Kurs** (≥ 200 EUR unter Stop) |
| `sap-delayed` | SAP SE `DE0007164600`, gettex, `DelayedMinutes=15`, `Quality=Delayed`, 04.11.2026 09:00–11:00 MEZ: Trades (ohne Aggressor), M1-Kerzen, Quotes ohne Bid/Ask (→ angenommener Spread). Daten kommen in 15-Minuten-Dateien: `ReceivedUtc` = Veröffentlichung (≥ AsOf + 15 Min) + Abruflatenz | 548 | **Kein Fill vor dem Ordereingang** (`fillsOnlyFromMarketDataAfterUtc`), `FillQuality.Synthetic`, DAY-Limit, KI-Storno |

**Herkunft:** Die Börsen-Hosts (api.kraken.com, api.bitvavo.com) waren aus der Build-Umgebung gesperrt (Proxy 403).
Alle drei Szenarien sind daher **synthetisch**, aber deterministisch und in sich stimmig erzeugt:
`Musterdepot.Recorder generate-fixtures` mit Seed `20261109` (Szenario b: +1, c: +2). Der Generator nutzt nur
ganzzahligen Zufall und `decimal` – keine Gleitkomma-Funktionen –, damit die Dateien auf Linux und Windows bitgleich
entstehen. Ein Test stellt sicher, dass Generator und eingecheckte Dateien übereinstimmen.

**Zeitachse:** Dateien sind nach `ReceivedUtc` (= Replay-Uhr) sortiert, `AsOfUtc` ist je Symbol nicht fallend.
Krypto: `ReceivedUtc` = AsOf + 25–90 ms. Aktie: siehe oben. Status-Ereignisse tragen Symbol `*`.

Neu erzeugen (nach Änderung am Generator):

```bash
dotnet run --project tools/Musterdepot.Recorder -- generate-fixtures      # schreibt tests/data/replays
UPDATE_GOLDEN=1 dotnet test tests/Musterdepot.IntegrationTests            # Golden-Statistik nachziehen
git diff --stat tests/data                                                # Änderungen prüfen, committen
```

`tests/data/.gitattributes` erzwingt LF für `*.jsonl`/`*.json`.

## Aufzeichnungsformat (`MarketEventJsonl`)

Eine Zeile je `MarketEvent` (System.Text.Json, `JsonDefaults.Options`: camelCase, Enums als Text, `decimal` exakt,
`null` weggelassen), Diskriminator `$type`: `trade`, `quote`, `book`, `candle`, `status`, `gap`. Endung `.jsonl` oder
`.jsonl.gz` (gzip). Lesen: `MarketEventJsonl.ReadAll/ReadAsync`; schreiben: `WriteAsync` bzw. `Writer` (Anhängen).

```json
{"$type":"trade","price":89416.6,"quantity":0.01468103,"aggressorSide":"Buy","tradeId":"81234572","symbol":"BTC/EUR","source":"kraken","asOfUtc":"2026-11-02T10:00:10.178+00:00","receivedUtc":"2026-11-02T10:00:10.246+00:00","delayedMinutes":0,"quality":"Realtime"}
```

Buch-Deltas: `isSnapshot=false`, nur geänderte Stufen, Menge `0` = Stufe entfernen.

## Orderskript-Format (`<szenario>.orders.json`)

Ein Orderskript beschreibt alles, was neben der Aufzeichnung für einen Golden Replay nötig ist. Zeitpunkte beziehen
sich auf die **Replay-Uhr** (`ReceivedUtc` der Marktereignisse).

```jsonc
{
  "formatVersion": 1,                 // Pflicht, derzeit 1
  "scenario": "btc-eur-gap",          // = Dateiname ohne .orders.json
  "description": "…",
  "replay": "btc-eur-gap.jsonl",      // relativ zum Skript (.jsonl oder .jsonl.gz)
  "seed": 20261110,                   // Generator-Seed, 0 = echte Aufzeichnung
  "instruments": [ { /* Core.Domain.Instrument */ "symbol": "BTC/EUR", "name": "Bitcoin / Euro", "assetClass": "Crypto", "quoteCurrency": "EUR", "tickSize": 0.1, … } ],
  "accounts": [ {                     // → OpenAccountRequest
      "accountId": "main", "name": "Testdepot", "initialCash": 100000, "baseCurrency": "EUR",
      "risk": null,                   // optional RiskConfig, sonst Standard
      "cryptoFees": { "kind": "Percent", "makerRate": 0.0025, "takerRate": 0.004, … },
      "stockFees":  { "kind": "Fixed", "fixedFee": 1, … } } ],
  "steps": [
    {
      "atUtc": "2026-11-03T10:02:00+00:00",   // Pflicht, UTC, Schritte aufsteigend
      "action": "PlaceOrder",                 // PlaceOrder | Cancel | Modify
      "accountId": "main",
      "source": "Human",                      // Human | Ai  (→ EngineCommand.Source / ICommand.Source)
      "clientName": "claude-code",            // optional; ICommand.ClientId = clientName ?? ("ui" | "ai-test")
      "placeOrder": {                         // Felder wie PlaceOrderRequest (ohne AccountId/Source/ClientName) + dryRun
        "clientOrderId": "g-stop-1",          // Pflicht, eindeutig; = IdempotencyKey des PlaceOrderCommand
        "symbol": "BTC/EUR", "side": "Sell", "type": "Stop",
        "quantity": 0.1,                      // genau eines von quantity / notional
        "stopPrice": 88770,                   // limitPrice bei Limit/StopLimit, stopPrice bei Stop/StopLimit
        "timeInForce": "Gtc",                 // Day | Gtc | Ioc | Fok | Gtd (+ expiresUtc)
        "strategyTag": null, "trailAmount": null, "trailPercent": null,
        "takeProfitPrice": null, "stopLossPrice": null, "ocoGroupId": null,
        "dryRun": false
      },
      "expect": {                             // optional, Plausibilisierung (Golden-Datei ist die eigentliche Prüfung)
        "accepted": true,                     // Befehl angenommen?
        "finalStatus": "Filled",              // OrderStatus der (Ziel-)Order am Replay-Ende
        "executedDuringGap": true,            // Fill.ExecutedDuringGap
        "minFillPrice": null, "maxFillPrice": 88770,
        "fillQuality": null,                  // OrderBook | Trade | Synthetic | Gap
        "fillsOnlyFromMarketDataAfterUtc": null  // alle Fill.MarketDataAsOfUtc > Zeitpunkt
      },
      "note": "Freitext"
    },
    { "atUtc": "…", "action": "Cancel", "accountId": "main",
      "cancel": { "clientOrderId": "g-lmt-cancel", "reason": "user" } },     // → CancelOrderRequest (orderId alternativ)
    { "atUtc": "…", "action": "Modify", "accountId": "main",
      "modify": { "clientOrderId": "…", "newLimitPrice": 89000 } }           // → ModifyOrderRequest
  ]
}
```

Regeln (geprüft von `OrderScriptValidator`): genau ein Nutzdatenfeld passend zu `action`; Cancel/Modify verweisen auf
eine **vorher** platzierte `clientOrderId` (deterministisch – die `OrderId` vergibt die Engine); Schritte innerhalb der
Aufzeichnung und zeitlich sortiert; Symbole unter `instruments` und in der Aufzeichnung vorhanden; Konto existiert.
Umsetzung: `step.ToEngineRequest(resolve)` (Engine direkt) bzw. `step.ToCommand(resolve, index)` (Befehlsschicht),
`resolve` = ClientOrderId → OrderId (z. B. `engine.GetOrderByClientOrderId(...)?.OrderId`).

## Golden-Replay-Format

`GoldenReplay.Run(fixture)` setzt das Orderskript in ein `Musterdepot.Engine.Backtest.BacktestScript` um und spielt es
mit dem `BacktestRunner` ab: frische `TradingEngine`, `ManualClock`, `SequentialIdGenerator`, Instrumente aus dem
Skript (`StaticInstrumentResolver`), Replay-Uhr = `ReceivedUtc`, vor jedem Schritt/Ereignis `OnClockTick`, Schritte vor
Ereignissen mit späterer Uhrzeit. Ergebnis `tests/data/golden/replays/<szenario>.json`:

```jsonc
{
  "scenario": "btc-eur-gap",
  "results": [ { "step": 0, "atUtc": "…", "action": "PlaceOrder", "source": "Human", "clientOrderId": "g-entry-1",
                 "accepted": true, "rejectReason": null, "riskRule": null } ],
  "orders":  [ /* Endzustand aller Orders */ ],
  "fills":   [ /* Fill inkl. Quality, ExecutedDuringGap, MarketDataAsOfUtc */ ],
  "portfolio": { /* PortfolioSnapshot am Ende */ },
  "stats":   { /* PerformanceStats */ },
  "events":  [ /* alle DomainEvents in Reihenfolge (JsonDefaults) */ ]
}
```

IDs kommen aus `SequentialIdGenerator`, Zeitstempel aus der Replay-Uhr – dadurch ist die Datei stabil. Gewollte
Änderungen: `UPDATE_GOLDEN=1 dotnet test tests/Musterdepot.IntegrationTests`, Diff prüfen, committen.

## Echte Daten aufzeichnen (`tools/Musterdepot.Recorder`)

```bash
dotnet run --project tools/Musterdepot.Recorder -- record --symbols BTC/EUR,ETH/EUR --minutes 60 \
    --out recordings/btc.jsonl.gz --sources kraken,bitvavo
# → recordings/btc-20261109-14.jsonl.gz, …-15.jsonl.gz (stündlich), Strg+C beendet sauber
dotnet run --project tools/Musterdepot.Recorder -- inspect recordings/btc-*.jsonl.gz
dotnet run --project tools/Musterdepot.Recorder -- slice recordings/btc-20261109-14.jsonl.gz \
    --from +10m --to +40m --symbols BTC/EUR --out tests/data/replays/btc-eur-real.jsonl
```

- `record` (Dauer per `--minutes` und/oder `--seconds`, sonst bis Strg+C) holt `IMarketDataHub` per DI (`AddMusterdepotMarketData(new DataSourceSettings{…})`); ist keiner registriert,
  endet es mit Exitcode 3 und einer klaren Meldung. Quellen: `kraken, bitvavo, coinbase, gettex, cboe, ecb, fake`
  (`fake` zum Testen der Kette ohne Netz). Ohne `--out`: `%LOCALAPPDATA%\Musterdepot\recordings\` (bzw. `MUSTERDEPOT_DATA_DIR`).
- `inspect` meldet Ereignisse je Typ/Symbol/Quelle/Qualität, Zeitraum (AsOf und Empfang), Rücksprünge, Lücken
  (`silence` > `--gap-seconds`, `gap-detected`, `missing-candles`) und kaputte bzw. abgeschnittene Zeilen (`--json` für Maschinen).
- `slice` schneidet `[from, to)` nach AsOf (oder `--by received`) aus; Status-Ereignisse bleiben erhalten.
- Für ein neues Golden-Szenario aus echten Daten: ausschneiden, `<name>.orders.json` von Hand schreiben (`seed: 0`),
  `instruments` ergänzen, Test laufen lassen (Validator), Golden mit `UPDATE_GOLDEN=1` anlegen.

## Runde 2 – Stand

- [x] **Golden Replays** je Szenario inkl. `expect`-Felder (`GoldenReplayTests`).
- [x] **Eigenschaftstests über DI** (`DiPropertyTests`, `RuntimeEndToEndTests.Restart_from_sqlite_equals_live_state`).
- [x] **Oberfläche ↔ MCP** (`UiMcpParityTests`).
- [x] **Runtime Ende-zu-Ende** inkl. Freigabe (confirm) und Not-Aus (`RuntimeEndToEndTests`).
- [x] **Recorder** `record --sources fake` (`RecorderFakeSourceTests`).
- [ ] Echte Kraken/Bitvavo-Aufzeichnung als zusätzliches Szenario (braucht Netz; Börsen-Hosts in der Build-Umgebung gesperrt).
- [ ] DAY-Order verfällt zum gettex-Handelsschluss in einem Replay über den ganzen Handelstag (heute: Engine-Tests von WS2).
- [ ] Hinweis: `RecordedMarketDataSource` spielt Ereignisse zum `AsOfUtc` ab (nicht zum aufgezeichneten `ReceivedUtc`).
  Für verzögerte Aufzeichnungen erscheinen Kurse im Runtime-Replay daher früher als im Golden Replay (`BacktestRunner`
  nutzt `ReceivedUtc`). Für Paritäts- und Neustart-Tests unerheblich; für „so wie damals erlebt“-Replays ggf.
  `EmitTime` auf `ReceivedUtc` umstellen (WS1).

## Abnahmekriterien der Roadmap (Checkliste)

1. **Grundgerüst** – ◇ Fake-Kurse laufen durch Oberfläche und Engine
   - [x] Solution, Core, Schnittstellen A–D, Projektskelette
   - [x] CI (Linux + Windows), Installer-Pipeline (Velopack), Testgerüst, Fixtures
   - [x] Fake-Quelle → Hub → Recorder (`RecorderFakeSourceTests`); Replay-Quelle → Hub → Runtime → Engine → SQLite (`RuntimeEndToEndTests`)
2. **Krypto live** – ◇ Golden Replays grün, Geld bleibt erhalten
   - [ ] Kraken + Bitvavo aufzeichnen (`record`), `inspect` ohne Rücksprünge/Lücken (braucht Netz)
   - [x] Golden Replays `btc-eur-quiet`, `btc-eur-gap` grün (Market/Limit/Stop/Stop-Limit/Notional, Gebühren, Depot)
   - [x] Eigenschaftstest „Geld bleibt erhalten“ grün (cent-genau, über DI und im Golden Replay)
3. **Aktien verzögert** – ◇ Kein Fill vor dem Ordereingang
   - [x] Golden Replay `sap-delayed` grün
   - [x] Jeder Fill hat `MarketDataAsOfUtc` > Ordereingang, Qualität `Synthetic`
4. **MCP + KI** – ◇ Oberfläche und MCP liefern gleiche Ergebnisse
   - [x] Vergleichstest ICommandBus (Human) ↔ MCP-Tools je Szenario grün
   - [x] MCP-Tests: nur 127.0.0.1, Token, Origin/Host-Prüfung (`Musterdepot.Mcp.Tests`), Not-Aus nur per Mensch lösbar, Freigabe im Modus confirm (`IntegrationTests`)
   - [ ] `.mcpb` wird im Release gebaut
5. **Ausbau** – Bracket, OCO, Statistik KI vs. Mensch, autonomer Modus, Backtests
   - [ ] Szenarien für Bracket/OCO/Trailing, Statistik getrennt nach `Source`, Backtest über aufgezeichnete Tage
