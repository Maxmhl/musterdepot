# Musterdepot.Core

Gemeinsame Datentypen und die vier fest vereinbarten Schnittstellen. Änderungen hier nur gemeinsam.

| Schnittstelle | Datei |
|---|---|
| A – `IMarketDataSource`, `IMarketDataHub`, `MarketEvent` | `MarketData/` |
| B – `ICommand`, `ICommandBus`, Befehlsliste | `Commands/Commands.cs` |
| C – versionierte Events (`DomainEvent` …) | `Events/DomainEvents.cs` |
| D – Lesemodelle `IReadModels`, `ITradingQueries` | `ReadModels/ReadModels.cs` |

Engine-Vertrag: `Abstractions/ITradingEngine.cs`. Infrastruktur (Uhr, IDs, Zufall, Persistence, Freigabe, Token, MCP-Host): `Abstractions/`.
