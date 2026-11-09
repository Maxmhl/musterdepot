# Claude-Desktop-Erweiterung (.mcpb)

Claude Desktop spricht MCP über stdio. Die Brücken-Exe `Musterdepot.McpBridge.exe` übersetzt stdio ↔ Named Pipe
(`Musterdepot.Mcp`, nur eigener Windows-Benutzer) zum laufenden Musterdepot. Bequem installierbar ist sie als
`.mcpb`-Erweiterung (Doppelklick bzw. Claude Desktop → Einstellungen → Erweiterungen).

## Zuständigkeit

| Teil | Wer | Ort |
|---|---|---|
| `manifest.json`, Icon, Beschreibung, Tool-Liste | Workstream 3b | `src/Musterdepot.McpBridge/mcpb/` |
| Packen, Version setzen, an Release anhängen | Workstream 5 | `build/mcpb/pack-mcpb.ps1`, `.github/workflows/release.yml` |

## Erwartung an das Manifest

- Einstiegspunkt der Brücke liegt im Paket unter `server/Musterdepot.McpBridge.exe`
  (`server.type = "binary"`, `server.entry_point` bzw. `server.mcp_config.command` mit `${__dirname}/server/Musterdepot.McpBridge.exe`).
- `version` wird beim Packen auf die Release-Version gesetzt; der Wert im Repository ist egal.
- Plattform: `win32`.

## Lokal packen

```powershell
# Node.js (für npx) und .NET 10 SDK nötig
./build/mcpb/pack-mcpb.ps1 -Version 0.2.0
# → artifacts/mcpb/Musterdepot-0.2.0.mcpb
```

Das Skript kopiert `src/Musterdepot.McpBridge/mcpb/*` in `artifacts/mcpb-staging/`, veröffentlicht die Brücke als
Einzeldatei (win-x64, self-contained) nach `server/`, prüft das Manifest mit `mcpb validate` und packt mit
`mcpb pack` (npm-Paket `@anthropic-ai/mcpb`).

## Im Release

`release.yml` führt das Skript nach dem Velopack-Upload aus, sobald `src/Musterdepot.McpBridge/mcpb/manifest.json`
existiert, und hängt die `.mcpb` an das GitHub Release des Tags an. Die installierte App enthält die Brücke zusätzlich
neben `Musterdepot.exe` (für manuelle Einträge in `claude_desktop_config.json`).
