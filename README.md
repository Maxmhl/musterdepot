# Musterdepot

Paper-Trading-Programm für Windows mit lokalem MCP-Server. Ein Prozess: Marktdaten, Paper-Trading-Engine, WPF-Oberfläche und MCP-Server. Bauplan: [docs/Bauplan.md](docs/Bauplan.md).

## Aufbau
```
src/Musterdepot.Core         Datentypen, Schnittstellen A–D
src/Musterdepot.Engine       Paper-Trading-Engine (Decide/Evolve)
src/Musterdepot.MarketData   Kraken, Bitvavo, Coinbase, gettex, EZB, Fake, Replay
src/Musterdepot.Persistence  SQLite (EF Core)
src/Musterdepot.Commands     einzige Befehlsschicht, Risiko, Freigabe, Protokoll
src/Musterdepot.Mcp          MCP-Server auf 127.0.0.1
src/Musterdepot.McpBridge    stdio ↔ Named Pipe für Claude Desktop
src/Musterdepot.App          WPF-Oberfläche
tests/                       xUnit-Tests
tools/Musterdepot.Recorder   Krypto-Daten für Golden Replays aufzeichnen
```

## Bauen
```
dotnet build Musterdepot.slnx
dotnet test Musterdepot.slnx
```
Die App (`net10.0-windows`, WPF) läuft nur unter Windows; alle anderen Projekte bauen und testen auch unter Linux/macOS.

## Stand
Alle fünf Workstreams des Bauplans sind umgesetzt (Roadmap-Phasen 1–5 im Code):
Marktdaten (Kraken, Bitvavo, Coinbase, gettex inkl. Replay, Fake, EZB), Engine (Market/Limit/Stop/Stop-Limit, Trailing, OCO, Bracket,
Orderbuch-Walk, Latenz, verzögerte Aktien, Lücken, Gebühren, FIFO, Risiko-Gate, Statistik, Backtest), SQLite-Persistence,
Befehlsschicht (Freigabe, Protokoll, Modi read_only/confirm/autonomous), MCP-Server (25 Tools, nur 127.0.0.1, Token) mit
Brücke für Claude Desktop, WPF-Oberfläche (7 Bildschirme, Lightweight Charts, ScottPlot, Tray, Not-Aus) sowie Tests, CI und Velopack.

Tests: `dotnet test Musterdepot.slnx` – 383 grün (Unit, Golden Replays, Eigenschaftstests, Oberfläche-vs-MCP-Parität, Ende-zu-Ende).

## Starten (Windows)
```
dotnet run --project src/Musterdepot.App
```
In den Einstellungen einen KI-Client anlegen, Token kopieren und z. B. in Claude Code eintragen:
```
claude mcp add --transport http papertrade http://127.0.0.1:47123/mcp --header "Authorization: Bearer <token>" --scope user
```
Weitere Clients: [docs/KI-Clients.md](docs/KI-Clients.md). Tests: [docs/Tests.md](docs/Tests.md). Installer: [docs/Installer.md](docs/Installer.md).

## Offen / nicht verifiziert
- Die WPF-App wurde nur gebaut, nicht unter Windows gestartet (Layout, WebView2-Chart, Tray, Benachrichtigungen ungetestet).
- Börsen-Endpunkte sind nur mit Beispieldaten getestet; gettex-URL und Dateinamensschema sind Annahmen (`GettexOptions`).
- Windows-spezifisch nur kompiliert: DPAPI-Token, Pipe-Rechte der Brücke, CI-/Release-Workflows, `vpk pack`.
- Replay-Testdaten sind synthetisch (deterministisch erzeugt); echte Aufzeichnung per `tools/Musterdepot.Recorder record`.
