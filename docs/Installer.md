# Installer und Updates (Velopack)

Musterdepot wird mit [Velopack](https://velopack.io) verteilt: eine `Setup.exe` ohne Adminrechte, Installation unter
`%LOCALAPPDATA%\Musterdepot`, Updates (inkl. Delta-Paketen) über GitHub Releases.

## Paketinhalt

| Datei | Herkunft |
|---|---|
| `Musterdepot.exe` + Laufzeit | `dotnet publish src/Musterdepot.App -r win-x64 --self-contained -p:PublishReadyToRun=true` |
| `Musterdepot.McpBridge.exe` | gleiche Optionen, in denselben Ordner (Brücke für Claude Desktop) |
| `Update.exe` | von Velopack beim Packen ergänzt |

Self-contained: auf dem Zielrechner muss kein .NET installiert sein. ReadyToRun verkürzt den Start.

## Release bauen

**Automatisch:** Tag pushen.

```bash
git tag v0.2.0 && git push origin v0.2.0        # Vorabversion: v0.3.0-beta.1 (wird als Pre-Release markiert)
```

`.github/workflows/release.yml` (windows-latest):

1. Version aus dem Tag (`v0.2.0` → `0.2.0`, SemVer-Prüfung), Tests.
2. `dotnet publish` von App und Brücke nach `artifacts/publish` (`-p:Version=…`).
3. `dotnet tool install -g vpk` – Version über die Repository-Variable `VPK_VERSION` festlegen; sie **muss zur Version
   des `Velopack`-NuGet-Pakets der App passen**. Ohne Variable wird die neueste vpk-Version genommen.
4. `vpk download github` holt die letzte Version (für Delta-Pakete; beim ersten Release übersprungen).
5. `vpk pack --packId Musterdepot --packVersion <v> --packDir artifacts/publish --mainExe Musterdepot.exe --channel win`
   → `Releases/`: `Musterdepot-win-Setup.exe`, `Musterdepot-win-Portable.zip`, `*-full.nupkg`, `*-delta.nupkg`, `releases.win.json`.
6. `vpk upload github --publish --tag v<v>` legt das GitHub Release an und lädt alles hoch (mit `GITHUB_TOKEN`,
   `permissions: contents: write`).
7. Falls `src/Musterdepot.McpBridge/mcpb/manifest.json` existiert: `.mcpb` bauen und anhängen (siehe `build/mcpb/README.md`).

Manuell (`workflow_dispatch`) läuft alles bis einschließlich `vpk pack`; das Ergebnis liegt als Workflow-Artefakt vor, nichts wird veröffentlicht.

**Lokal (Windows):**

```powershell
dotnet tool install -g vpk
./build/pack.ps1 -Version 0.2.0                       # publish + vpk pack → Releases/
./build/pack.ps1 -Version 0.3.0-beta.1 -Channel beta  # eigener Kanal
./build/pack.ps1 -Version 0.2.1 -RepoUrl https://github.com/OWNER/REPO               # mit Delta gegen letzte Version
./build/pack.ps1 -Version 0.2.1 -RepoUrl https://github.com/OWNER/REPO -Upload       # zusätzlich hochladen ($env:GITHUB_TOKEN)
```

## Was die App dafür tun muss (Workstream 4)

1. NuGet `Velopack` referenzieren (gleiche Version wie `vpk`).
2. Ganz am Anfang von `Main` bzw. vor dem Start von WPF: `VelopackApp.Build().Run();` – verarbeitet Install-/Update-/
   Deinstallations-Hooks und beendet sich dabei selbst. In WPF heißt das: eigene `Main`-Methode (Program.cs,
   `[STAThread]`) statt der generierten, danach `new App().Run()`.
3. Updates prüfen (z. B. beim Start und täglich, abschaltbar in den Einstellungen):

   ```csharp
   var mgr = new UpdateManager(new GithubSource("https://github.com/OWNER/REPO", accessToken: null, prerelease: false));
   if (mgr.IsInstalled && await mgr.CheckForUpdatesAsync() is { } update)
   {
       await mgr.DownloadUpdatesAsync(update);
       // Hinweis in der Oberfläche; bei Bestätigung (offene Orders/Replay beachten):
       mgr.ApplyUpdatesAndRestart(update);
   }
   ```

   Vor dem Neustart sauber herunterfahren (`IMusterdepotRuntime.StopAsync`, SQLite schließen, MCP-Server stoppen).
   Bei Start aus dem Debugger/Publish-Ordner ist `IsInstalled` false – dann keine Updatesuche.

## Installation ohne Adminrechte

- `Musterdepot-win-Setup.exe` installiert nach `%LOCALAPPDATA%\Musterdepot\` (`current\` = aktuelle Version,
  `Update.exe` daneben), legt Startmenü- und Desktop-Verknüpfung an und startet die App. Kein UAC-Dialog.
- Die Benutzerdaten liegen getrennt unter `%LOCALAPPDATA%\Musterdepot\` (`AppPaths.DataDirectory`: `musterdepot.db`,
  `recordings\`, `tokens\`, `logs\`) – Velopack verwaltet nur seine eigenen Ordner (`current\`, `packages\`, `Update.exe`).
  Deinstallieren entfernt die Programmdateien; ob die Daten mit gelöscht werden, entscheidet der
  Deinstallations-Hook der App (Standard: behalten, Hinweis in den Einstellungen).
- SmartScreen: unsignierte Setup.exe zeigt „Unbekannter Herausgeber“. Code-Signing kann später über die
  `vpk pack`-Optionen zur Signierung ergänzt werden (Zertifikat als GitHub-Secret).
- Portable: `Musterdepot-win-Portable.zip` läuft ohne Installation (Updates dann ebenfalls über Velopack im Ordner).

## Autostart ohne Admin

Einstellungen → „Mit Windows starten“ schreibt bzw. löscht den Wert im Benutzer-Run-Schlüssel (kein Admin nötig):

```
HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run
  Musterdepot = "%LOCALAPPDATA%\Musterdepot\current\Musterdepot.exe" --minimized
```

- Den Pfad zur Laufzeit ermitteln (`Environment.ProcessPath`) und nur bei installierter App (`UpdateManager.IsInstalled`)
  anbieten; `current\` bleibt über Updates hinweg gleich, der Eintrag muss nach Updates nicht angepasst werden.
- `--minimized` startet direkt im Infobereich; Datenverbindungen laufen im Hintergrund.
- Beim Deinstallieren den Wert entfernen (Velopack-Hook `WithBeforeUninstallFastCallback`), sonst bleibt ein toter Eintrag.
- Nach Updates startet Velopack die App selbst neu; der Run-Eintrag greift erst beim nächsten Anmelden.

## Updates über GitHub Releases

- Kanal `win` (stabil) und optional `beta`: `vpk pack/upload --channel beta`, App wählt den Kanal über
  `UpdateOptions.ExplicitChannel`. Pre-Releases (Tag mit `-`) werden mit `--pre` hochgeladen und nur von Clients mit
  `prerelease: true` gesehen.
- Delta-Pakete entstehen automatisch, wenn die vorherige Version vor `vpk pack` im Ausgabeordner liegt (`vpk download github`).
- Für ein privates Repository braucht die App ein Lese-Token – für Musterdepot ist ein öffentliches Release-Repository vorgesehen.

## Prüfliste vor dem ersten Release

- [ ] App ruft `VelopackApp.Build().Run()` vor WPF auf (WS4)
- [ ] `VPK_VERSION` als Repository-Variable = Velopack-NuGet-Version der App
- [ ] Testinstallation auf einem Windows-Konto ohne Adminrechte: Setup, Start, Autostart an/aus, Update von n−1 auf n, Deinstallation
- [ ] Brücke liegt neben `Musterdepot.exe`; Claude Desktop erreicht die Named Pipe
- [ ] `.mcpb` im Release vorhanden (sobald WS3b das Manifest liefert)
