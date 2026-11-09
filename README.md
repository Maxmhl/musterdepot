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
