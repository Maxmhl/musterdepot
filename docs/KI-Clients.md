# KI-Clients mit Musterdepot verbinden

Musterdepot enthält einen MCP-Server, der **nur lokal** auf diesem PC erreichbar ist:

```
http://127.0.0.1:47123/mcp      (Streamable HTTP, zustandslos, Port in den Einstellungen änderbar)
\\.\pipe\Musterdepot.Mcp         (Named Pipe für die Claude-Desktop-Brücke, nur eigener Windows-Benutzer)
```

Der Server läuft im Programm selbst – Musterdepot muss also geöffnet sein (Schließen minimiert in den Infobereich, der Server läuft weiter). Alle Aktionen der KI laufen über dieselbe Befehlsschicht wie die Oberfläche: Limits, Freigabe-Modus, Ratenbegrenzung, Tagesverlustgrenze und Not-Aus gelten auch für die KI. Jeder Tool-Aufruf steht im Protokoll (KI-Aktivität).

## 1. Token erzeugen

Einstellungen › KI-Clients › „Neuer Client“: Name (z. B. „Claude Code“), Rechte und Modus wählen. Das Token (`mdp_…`) wird **einmal** angezeigt – sofort kopieren.

| Recht | erlaubt |
|---|---|
| `read` | alle Lese-Tools, Watchlists/Alarme, `place_order` mit `dry_run`, `engage_kill_switch` |
| `trade` | Orders aufgeben, ändern, stornieren |
| `settings` | `update_settings` (unkritische Einstellungen) |

| Modus (nur im Programm änderbar) | Wirkung |
|---|---|
| `read_only` | nur Lesen |
| `confirm` | jede Order erzeugt eine Windows-Benachrichtigung Erlauben/Ablehnen; die KI bekommt `pending_approval` |
| `autonomous` | Orders ohne Rückfrage, aber innerhalb der harten Limits |

Speicherung: Im Einstellungsdatensatz steht nur der SHA-256-Hash des Tokens. Das Token selbst liegt per Windows-DPAPI (aktueller Benutzer) verschlüsselt unter `%LOCALAPPDATA%\Musterdepot\tokens\<client-id>.token`, damit es im Programm später erneut angezeigt werden kann. Widerrufen = Client löschen; das Token ist sofort ungültig (auch bei offener Brücken-Verbindung).

> Entwicklungsmodus: Unter Linux/macOS (nur für Entwicklung und Tests) gibt es kein DPAPI – das Token liegt dort im Klartext in einer Datei mit Rechten 600.

## 2. Claude Code

```
claude mcp add --transport http papertrade http://127.0.0.1:47123/mcp --header "Authorization: Bearer <token>" --scope user
```

Prüfen mit `claude mcp list` bzw. in einer Sitzung mit `/mcp`. Bei geändertem Port die URL anpassen.

## 3. Claude Desktop

Claude Desktop startet lokale Server über stdio. Dafür gibt es die Brücke **`Musterdepot.McpBridge.exe`** (liegt im Installationsordner von Musterdepot). Sie verbindet stdin/stdout byte-genau mit der Named Pipe des laufenden Programms. Läuft Musterdepot nicht, schreibt sie eine Fehlermeldung auf stderr (sichtbar im MCP-Log von Claude Desktop) und beendet sich mit Code 2.

### Variante A: Erweiterung (.mcpb) – bequem

Vorlage: `src/Musterdepot.McpBridge/mcpb/manifest.json`. Paket bauen:

```
dotnet publish src/Musterdepot.McpBridge -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o mcpb-build/server
copy src\Musterdepot.McpBridge\mcpb\manifest.json mcpb-build\
npx @anthropic-ai/mcpb pack mcpb-build musterdepot.mcpb
```

`musterdepot.mcpb` per Doppelklick bzw. Claude Desktop › Einstellungen › Erweiterungen installieren. Beim Installieren fragt Claude Desktop nach dem Token (wird als sensibler Wert gespeichert und der Brücke als `MUSTERDEPOT_TOKEN` übergeben).

### Variante B: `claude_desktop_config.json`

Datei: `%APPDATA%\Claude\claude_desktop_config.json` (Claude Desktop › Einstellungen › Entwickler › Konfiguration bearbeiten).

```json
{
  "mcpServers": {
    "musterdepot": {
      "command": "C:\\Users\\<name>\\AppData\\Local\\Musterdepot\\current\\Musterdepot.McpBridge.exe",
      "args": [],
      "env": { "MUSTERDEPOT_TOKEN": "mdp_..." }
    }
  }
}
```

Statt der Umgebungsvariablen geht auch `"args": ["--token", "mdp_..."]`. Weitere Argumente: `--pipe <name>` (Standard `Musterdepot.Mcp`), `--timeout <sekunden>` (Verbindungsaufbau, Standard 5).

### Entscheidung: Authentifizierung der Brücke

- **Token bevorzugt (Standard).** Die Brücke sendet als erste Zeile einen Handshake `{"type":"musterdepot-bridge","version":1,"token":"mdp_…"}`; das Programm antwortet mit `{"ok":true,"client_id":…,"name":…}` oder `{"ok":false,"error":…}`. Danach fließen nur noch MCP-Nachrichten. So gelten für Claude Desktop dieselben Rechte, Modi, Protokolleinträge und derselbe Widerruf wie für HTTP-Clients.
- Die Pipe selbst ist zusätzlich auf den eigenen Windows-Benutzer beschränkt (ACL mit der SID des Benutzers, Netzwerkzugriff verweigert).
- **Tokenlos (optional, aus):** Mit `McpOptions.AllowTokenlessPipeClient = true` akzeptiert das Programm Pipe-Verbindungen ohne Token und verwendet dann den in den Einstellungen angelegten Client `claude-desktop` (Rechte/Modus von dort). Nur sinnvoll, wenn man auf das Token in der Claude-Konfiguration verzichten will.

| Exit-Code der Brücke | Bedeutung |
|---|---|
| 0 | normal beendet |
| 1 | falsche Argumente |
| 2 | Programm läuft nicht / Pipe nicht erreichbar |
| 3 | Token abgelehnt oder Handshake fehlgeschlagen |

## 4. LM Studio

LM Studio › Programm › Integrations › „Edit mcp.json“ (oder `%USERPROFILE%\.lmstudio\mcp.json`):

```json
{
  "mcpServers": {
    "musterdepot": {
      "url": "http://127.0.0.1:47123/mcp",
      "headers": { "Authorization": "Bearer mdp_..." }
    }
  }
}
```

Hinweis: Kleine lokale Modelle kommen mit 25 Tools nicht immer gut zurecht – ggf. einen Client nur mit Recht `read` anlegen und Orders über `dry_run` vorschlagen lassen.

## 5. Open WebUI

Open WebUI › Admin-Einstellungen › Externe Tools › Server hinzufügen: Typ **MCP (Streamable HTTP)**, URL `http://127.0.0.1:47123/mcp`, Authentifizierung **Bearer** mit dem Token.

**Docker:** Im Container zeigt `127.0.0.1` auf den Container selbst, nicht auf den PC. Musterdepot lauscht absichtlich nur auf 127.0.0.1. Möglichkeiten:

1. Open WebUI ohne Docker betreiben (z. B. `pip install open-webui` / `uvx open-webui serve`) – einfachster Weg.
2. Container mit Host-Netzwerk starten (`--network=host`; bei Docker Desktop muss „Host networking“ aktiviert sein). Dann funktioniert die URL oben unverändert.
3. Über `http://host.docker.internal:47123/mcp`: Dann sendet Open WebUI den Host-Header `host.docker.internal:47123`, den Musterdepot zum Schutz vor DNS-Rebinding mit **403** ablehnt. Die Host-Prüfung muss dafür erweitert werden (`McpOptions.AdditionalAllowedHosts.Add("host.docker.internal:47123")`). Ob Docker Desktop Anfragen an `host.docker.internal` an einen nur auf 127.0.0.1 lauschenden Dienst weiterleitet, hängt von Version und Netzwerkmodus ab – Variante 1 oder 2 bevorzugen.

## 6. claude.ai im Browser

Nicht möglich. Connectors auf claude.ai werden von Anthropics Servern aus aufgerufen und brauchen einen öffentlich erreichbaren HTTPS-Server. Musterdepot ist bewusst nur lokal erreichbar (127.0.0.1, Host-/Origin-Prüfung) und wird nicht ins Internet freigegeben. Für Claude also **Claude Desktop** oder **Claude Code** verwenden.

## 7. Was die KI sieht

Tools (✱ = schreibend, geht über die Befehlsschicht):

| Bereich | Tools |
|---|---|
| Marktdaten | `search_symbol`, `get_quote`, `get_candles`, `get_orderbook` (nur Krypto) |
| Orders | ✱`place_order`, ✱`modify_order`, ✱`cancel_order`, `list_orders` |
| Depot | `get_portfolio`, `get_positions`, `get_trade_history`, `get_performance_stats` |
| Watchlists, Alarme | `list_watchlists`, `list_alerts`, ✱`create_watchlist`, ✱`add_to_watchlist`, ✱`remove_from_watchlist`, ✱`delete_watchlist`, ✱`create_alert`, ✱`delete_alert` |
| Einstellungen, Betrieb | `get_settings`, ✱`update_settings`, `get_audit_log`, `get_agent_status`, ✱`engage_kill_switch` |

- Jedes Ergebnis ist ein JSON-Objekt `{ "status", "message", "data", "audit_id", "approval_id", "requires_human_approval", "idempotency_key" }` (als `structuredContent` und als Text). Status: `ok`, `not_found`, `invalid_arguments`, `denied`, `error` bzw. für Befehle `executed`, `previewed`, `pending_approval`, `rejected`, `duplicate`, `failed`.
- Jeder Kurs trägt `source`, `delayed_minutes`, `as_of_utc`, `is_realtime` (plus `age_seconds`). Aktien sind 15–30 Minuten verzögert.
- `place_order` verlangt `idempotency_key`; Wiederholung mit gleichem Schlüssel erzeugt keine zweite Order. `dry_run: true` liefert eine Vorschau mit Gebühren und Risikoprüfung.
- `update_settings` ändert nur Theme, Autostart, Infobereich und Datenquellen – keine Limits, Gebühren, Modi, Rechte oder den MCP-Port.
- `engage_kill_switch`: Die KI darf den Not-Aus auslösen, lösen kann ihn nur ein Mensch per Klick im Programm.
- Tools tragen MCP-Annotations (`readOnlyHint`, `destructiveHint`, `idempotentHint`).

## 8. Sicherheit (Kurzfassung)

- Kestrel bindet nur an `127.0.0.1` (nie `0.0.0.0`), Port > 1024.
- Host-Header muss `127.0.0.1:<port>` oder `localhost:<port>` sein; ein vorhandener `Origin` muss `http(s)://127.0.0.1|localhost[:port]` sein – sonst **403** (Schutz vor DNS-Rebinding und fremden Webseiten).
- Ohne gültiges Bearer-Token **401**. Rechte werden je Tool geprüft; Verweigerungen werden protokolliert.
- Named Pipe nur für den eigenen Windows-Benutzer.

## 9. Für Entwickler: Einbindung in die App

```csharp
services.AddMusterdepotMcp();                       // oder AddMusterdepotMcp(o => { o.Port = 0; o.EnablePipe = false; })
// nach dem Start der Runtime:
await provider.GetRequiredService<IMcpServerHost>().StartAsync();   // startet HTTP (Kestrel) und Named Pipe
// beim Beenden:
await provider.GetRequiredService<IMcpServerHost>().StopAsync();
```

`IMcpServerHost.Endpoint` liefert die tatsächliche URL (bei `Port = 0` zufällig). Ist `AppSettings.McpEnabled` aus, startet `StartAsync` nichts. Mit Generic Host alternativ `services.AddMusterdepotMcpHostedService()`.
