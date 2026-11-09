# Paper-Trading-Programm für Windows mit MCP-Server – Bauplan

Übertragung des Bauplans (PDF vom 9.11.2026) als Arbeitsgrundlage.

## Überblick
Musterdepot mit simuliertem Geld für Windows. Daten, Paper-Trading-Engine, Oberfläche und MCP-Server laufen in **einem Prozess** (C#/.NET 10), ohne Server und ohne bezahlte/anmeldepflichtige Datenquellen.
Krypto läuft in Echtzeit (eigentlicher Daytrading-Modus). Aktien ohne Anmeldung nur 15–30 Min verzögert, dafür Übungs- und Replay-Modus. KI nur lokal.

Architektur: Krypto-Feeds (Kraken, Bitvavo, Echtzeit) und Aktien-Dateien (gettex, 15–30 Min alt) → **MarketData** (WebSocket, CSV-Abruf, Reconnect, Nachladen) → **Engine** (Fills, Depot, Gebühren, Risiko-Gate) → **SQLite** (Events, Kerzen, KI-Protokoll). **Commands** (gleich für Oberfläche und KI, Freigabe, Protokoll) ist der einzige Weg, etwas zu ändern. **WPF-Oberfläche** (Charts, Orderticket, Depot, Freigabe, Not-Aus) und **MCP-Server** (nur 127.0.0.1/mcp, Token, Limits) gehen über Commands. KI-Clients: Claude Code, LM Studio, Claude Desktop über Brücke.

## Marktdaten
### Krypto in Echtzeit
| Quelle | Rolle | Kanäle ohne Schlüssel | Historie | Haken |
|---|---|---|---|---|
| Kraken WS v2 | Hauptquelle | Ticker, Trades, Kerzen, Orderbuch 10–1000 Stufen | REST max. 720 Kerzen (1 Min ≈ 12 h) | ~150 Verbindungen/10 Min/IP, sonst Sperre |
| Bitvavo | Historie + Ausweichquelle, EUR-Paare | Ticker, Trades, Kerzen, Orderbuch | 1440 Kerzen/Abruf, start/end weit zurück | IP-Limit ohne Schlüssel niedriger |
| Coinbase Exchange | Ausweichquelle | Ticker, Trades, level2_batch | 300 Kerzen/Abruf | volles level2 braucht Anmeldung |

Binance/Bybit scheiden aus (EU).

### Aktien
| Quelle | Verzögerung | Zugriff | Bewertung |
|---|---|---|---|
| gettex | bis 15 Min, neue Datei alle 15 Min mit letzten 24 h | CSV-Download | erlaubt für Privatpersonen, einzige saubere Wahl |
| Cboe Europe | bis 15 Min | CSV | zweite Quelle, ungeprüft |
| Yahoo, Tradegate, Börse Frankfurt, IEX, Stooq | – | – | nicht verwenden |

Folgen: Live-Krypto wird lokal mitgeschrieben (eigene Historie für Backtests). Aktien = Übungsmodus mit Verzögerung, jeder Kurs zeigt Datenstand + „verzögert“; Replay-Modus spielt gettex-Dateien des Tages ab. EUR/USD: täglicher EZB-Referenzkurs, untertags USDC/EUR bei Kraken als Näherung. Jede Quelle hinter eigener Schnittstelle, austauschbar.

## Windows-Programm
C# / .NET 10 (LTS bis 14.11.2028) + WPF. Offizielles MCP-SDK `ModelContextProtocol` 2.4.0 (Tier 1), `decimal` für Geld, Velopack-Installer ohne Adminrechte, Live-Charts über WebView2.

### Projektstruktur (Abhängigkeiten nur nach unten)
| Projekt | Aufgabe | Hängt ab von |
|---|---|---|
| Core | Datentypen: Instrument, Kerze, Order, Fill, Position, Geld als decimal | – |
| Engine | Ausführung, Gebühren, Depot, GuV, Uhr austauschbar | Core |
| MarketData | WebSocket-Clients, REST-Nachladen, Wiederverbinden | Core |
| Persistence | SQLite über EF Core | Core |
| Commands | einzige Befehlsschicht mit Prüfung, Risikolimits, Freigabe, Protokoll | Engine, MarketData, Persistence |
| Mcp | MCP-Server auf 127.0.0.1 | Commands |
| App | WPF-Oberfläche mit MVVM | Commands, Mcp |
| Tests | xUnit für Engine, Commands, MCP | alle |

### Charts
TradingView Lightweight Charts (Apache-2.0) in WebView2, Hinweis/Logo auf TradingView. Indikatoren (EMA, RSI, VWAP) rechnet C#, ans Chart gehen nur fertige Werte. Equity-Kurve mit ScottPlot (MIT).

### Betrieb im Hintergrund
- Schließen minimiert in den Infobereich; Datenverbindungen laufen weiter.
- Autostart optional über HKCU Run-Schlüssel, ohne Adminrechte.
- Standby: Uhrzeit speichern, sauber trennen. Aufwachen: neu verbinden, verpasste Kerzen per REST nachladen, Lücke im Chart markieren.
- Offene Stop-/Limit-Orders werden gegen die nachgeladenen Kerzen der Reihe nach geprüft; solche Fills tragen „während Lücke ausgeführt“.

### Datenhaltung
Eine SQLite-Datei unter `%LOCALAPPDATA%`. Tabellen: Konten, Instrumente, Watchlists, Orders, Fills, Positionen, Kontobuchungen, Kerzen, KI-Aktionen, Einstellungen. Geldbeträge als Text (keine Rundung). Rohe Ticks nur im RAM, 1-Min-Kerzen 90 Tage, längere Zeitrahmen unbegrenzt.

### Bildschirme
1. Watchlist: Live-Kurse, Änderung %, deutliche Markierung „verzögert“ bei Aktien
2. Chart: Zeitrahmen, Indikatoren, eigene Orders und Positionen als Preislinien
3. Orderticket: Market, Limit, Stop, Stückzahl oder Betrag, Gebührenvorschau
4. Depot: Positionen, GuV, Equity-Kurve
5. Orders und Historie: offene Orders, Ausführungen, CSV-Export
6. KI-Aktivität: Protokoll aller Tool-Aufrufe, Freigabe-Warteschlange, großer Not-Aus (auch im Tray-Menü)
7. Einstellungen: Datenquellen, MCP-Port und Token, Limits, Gebühren, Autostart

### Tests und Verteilung
- Engine-Tests mit fester Uhr und aufgezeichneten Kerzendateien
- Eigenschaftstests: Cash + Positionswert stimmt nach jeder Buchung
- MCP-Tests mit dem SDK-Client gegen den echten lokalen Server
- Velopack: Setup.exe ohne Adminrechte, Updates über GitHub Releases

## Paper-Trading-Engine
Reine C#-Bibliothek ohne Oberfläche/Netzwerk. Jede Order und jeder Fill ist ein Event; Depotstand = Abspielen der Events. Live, Replay und Backtest = derselbe Code mit anderer Datenquelle und Uhr.

### Ordertypen
| Stufe | Ordertypen | Gültigkeit |
|---|---|---|
| v1 | Market, Limit, Stop, Stop-Limit | DAY, GTC, IOC |
| v1.1 | Trailing Stop, OCO, Bracket (Einstieg + TP + SL) | wie v1 |
| später | FOK, GTD, Iceberg | GTD |

### Ausführungssimulation
- Krypto mit Orderbuch: Market-Orders laufen Stufe für Stufe durch das Kraken-Orderbuch (Teilausführungen, Durchschnittspreise).
- Limit: ruhende Limits füllen erst, wenn ein Trade echt durch das Limit geht, gedeckelt auf einen Anteil des gehandelten Volumens.
- Stop: ausgelöst durch den letzten Trade; bei Kurssprung zum ersten verfügbaren Kurs gefüllt, nicht zum Stop-Preis.
- Latenz: Orders erst gegen Daten nach 200 ms geprüft.
- Verzögerte Aktien: Ausführung nur gegen Trades mit Zeitstempel nach Ordereingang; angenommener Spread, Fill-Qualität `SYNTHETIC`.
- Replay-Modus: gettex-Dateien des Tages mit simulierter Uhr.
- Handelszeiten: Aktien nach gettex-Handelszeiten, Krypto 24/7, Feiertagskalender. Ohne frische Daten werden Market-Orders abgelehnt.

### Kosten und Buchhaltung
Gebührenmodelle: fest je Order (z. B. 1 €), prozentual Maker/Taker, gestaffelt, kombiniert. Nur `decimal`. Basiswährung EUR (USD-Paare zum Näherungskurs, Kurs im Event). Einstand nach FIFO, realisierter und offener Gewinn getrennt. Kein Leerverkauf in v1.

### Statistik
Gewinnquote, Profit Factor, Erwartungswert, max. Drawdown, Equity-Kurve, Haltedauer, Gebührenanteil. Jede Order trägt `Source` (Mensch/KI) und Strategie-Tag.

### Risikoregeln (Risiko-Gate vor jeder Order)
- max. Ordergröße und Positionsgröße, absolut und in % des Depots
- max. Tagesverlust, danach nur reduzierende Orders
- max. Orders pro Minute, für die KI strenger
- Preisband gegen Tippfehler bei Limits
- Not-Aus, das nur ein Mensch im Programm lösen kann

### Datenmodell und Tests
Kern-Typen: Instrument, Account, Order (mit ClientOrderId), Fill, Lot, CashEntry, RiskConfig, EquitySnapshot. Engine = zwei reine Funktionen: `Decide` (Befehl/Marktdaten → Events), `Evolve` (Events → Zustand). Uhr, IDs, Zufall von außen.
- Golden Replays: aufgezeichnete Kerzen/Trades + Orderskript → fester Event-Log
- Eigenschaftstests: Geld bleibt erhalten, Limit-Fills nie schlechter als Limit, keine negativen Mengen, Risiko-Gate wird nie umgangen

## MCP-Server lokal
`http://127.0.0.1:<port>/mcp` (Standard 47123), nur lokal. Brücken-Exe (stdio ↔ Named Pipe) für Claude Desktop. SDK `ModelContextProtocol` 2.4.0, zustandslos; ältere Clients (Spezifikation 2025-11-25) zusätzlich bedienen. Server läuft über Kestrel im Programm, gleiche Engine-Instanz, ruft nur Commands auf.

| Client | Weg |
|---|---|
| Claude Desktop | Brücken-Exe über stdio → Named Pipe; bequem als `.mcpb`-Erweiterung |
| Claude Code | direkt HTTP mit Token |
| LM Studio | `mcp.json` mit URL und Token |
| Open WebUI | Streamable HTTP mit Bearer-Token |

```
claude mcp add --transport http papertrade http://127.0.0.1:47123/mcp --header "Authorization: Bearer <token>" --scope user
```

### Sicherheit
- Nur an 127.0.0.1 binden, nie 0.0.0.0; Port > 1024.
- Origin- und Host-Header prüfen, fremde Anfragen mit 403 ablehnen (DNS-Rebinding).
- Zufälliges Token pro Client, per Windows-DPAPI verschlüsselt, Rechte `read`, `trade`, `settings`.
- Named Pipe nur für den eigenen Windows-Benutzer.

### Tool-Liste (✱ = Nebenwirkung)
| Bereich | Tools |
|---|---|
| Marktdaten | search_symbol, get_quote, get_candles, get_orderbook (nur Krypto) |
| Orders | ✱place_order, ✱modify_order, ✱cancel_order, list_orders |
| Depot | get_portfolio, get_positions, get_trade_history, get_performance_stats |
| Watchlists, Alarme | list_watchlists, list_alerts, ✱anlegen/ergänzen/entfernen/löschen |
| Einstellungen, Betrieb | get_settings, ✱update_settings (ohne Limits und Modus), get_audit_log, get_agent_status |

Jeder Kurs kommt mit `source`, `delayed_minutes`, `as_of_utc`, `is_realtime`. `place_order` verlangt `idempotency_key`, `dry_run` = Vorschau mit Gebühren und Risikoprüfung.

### Sicherheitsgrenzen für die KI
1. Modi pro Client (nur in der Oberfläche): `read_only`, `confirm` (Windows-Benachrichtigung Erlauben/Ablehnen), `autonomous` innerhalb harter Limits.
2. Limits: Ordergröße, Positionsgröße in % des Depots, Zahl offener Orders, erlaubte Symbole.
3. Ratenbegrenzung, z. B. max. 10 Orders/Minute.
4. Tagesverlustgrenze: danach automatisch `read_only` und offene Orders storniert.
5. Not-Aus: KI darf auslösen, lösen nur per Klick im Programm.
6. Protokoll jeder Aktion mit Zeit, Client, Parametern, Ergebnis und Freigebendem.

## Arbeitsaufteilung
| Workstream | Verantwortet | Projekte | Schnittstelle |
|---|---|---|---|
| 1 Marktdaten | Kraken/Bitvavo-WS, gettex-Abruf+Parser, Reconnect, Nachladen nach Standby | MarketData | A |
| 2 Engine | Ordertypen, Fill-Simulation inkl. Verzögerung/Replay, Gebühren, Buchhaltung, Risiko-Gate, Statistik | Core, Engine | A, C |
| 3 Befehle und MCP | Befehlsschicht, Freigabe, Protokoll, MCP-Server, Brücke, Tokens | Commands, Mcp, Brücken-Exe | B, C |
| 4 Oberfläche | WPF, Charts in WebView2, Orderticket, Depot, KI-Aktivität, Not-Aus, Tray | App | B, D |
| 5 Qualität | Daten aufzeichnen, Tests, Vergleich Oberfläche vs. MCP, Velopack | Tests, Build | alle |

Schnittstellen: **A** `IMarketDataSource` (Source, AsOfUtc, DelayedMinutes, Qualitätsflag), **B** `ICommand` (PlaceOrder, CancelOrder, AddToWatchlist … mit Source und IdempotencyKey), **C** Events (OrderSubmitted, OrderFilled, KillSwitchEngaged …), **D** Lesemodelle.

## Roadmap
1. Grundgerüst: Solution, Core, Schnittstellen A–D, WPF mit Fake-Daten, Installer ◇ Fake-Kurse laufen durch Oberfläche und Engine
2. Krypto live: Kraken + Bitvavo, mitschreiben, Market/Limit/Stop, Depot, Gebühren ◇ Golden Replays grün, Geld bleibt erhalten
3. Aktien verzögert: gettex abrufen/einlesen, Übungs- und Replay-Modus ◇ Kein Fill vor dem Ordereingang
4. MCP + KI: Server, Tokens, Freigabe per Toast, Brücke, Not-Aus ◇ Oberfläche und MCP liefern gleiche Ergebnisse
5. Ausbau: Bracket, OCO, Statistik KI vs. Mensch, autonomer Modus, Backtests

## Offene Entscheidungen – für die Umsetzung getroffene Annahmen
| Frage | Annahme (änderbar) |
|---|---|
| Aktien-Modus in v1? | Ja, als verzögerter Übungs- und Replay-Modus |
| Welcher KI-Client zuerst? | Claude Code (HTTP + Token); LM Studio über gleiche URL |
| Brücke für Claude Desktop in v1? | Ja, enthalten (`Musterdepot.McpBridge`) |
| KI und eigene Trades trennen? | Per `Source` + Strategie-Tag in einem Depot |
| Programmname | **Musterdepot** |
