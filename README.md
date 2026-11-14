<p align="center"><img src="branding/png/thixclip-full.png" alt="ThixClip" width="420"></p>

# ThixClip

**Der Zwischenablage-Verlauf für Windows und macOS** – schnell, komplett per Tastatur bedienbar und auf Wunsch über
**ThixClip.Cloud** auf allen deinen Computern gleich (auch im Browser).

- **Tastenkürzel**: `Strg + Win + V` (Windows) bzw. `⌃⌥V` (macOS) öffnet ThixClip aus jeder App – es hat Vorrang vor
  gleichen Kürzeln anderer Programme und ist in den Einstellungen änderbar.
- **Sofort da**: das Fenster wird beim Start einmal gebaut und danach nur noch ein- und ausgeblendet; die Suche läuft
  im Speicher bei jedem Tastendruck.
- **Texte, Links und Bilder** – mit **Texterkennung (OCR)**: die Suche findet auch Text in Screenshots
  (Windows-OCR bzw. Apple Vision, offline auf dem Gerät).
- **Favoriten und Anheften**: bleiben dauerhaft, alles andere nach der **Speicherdauer** (1 Tag … unbegrenzt).
- **Vertrauliches bleibt draußen**: was Passwort-Manager als vertraulich markieren, wird nie aufgezeichnet.
- **Tray / Menüleiste**, **Autostart**, **Setup** (Windows) bzw. **App-Bundle + DMG** (macOS), **OTA-Updates** mit
  optionalem **Full-Auto-Update**.
- **ThixClip.Cloud** (Docker, Port 4950): Einträge, Favoriten und Löschungen werden zwischen allen Geräten abgeglichen,
  im Browser (PWA) liest, sucht, kopiert und ergänzt man den Verlauf auch unterwegs.

| Verlauf (hell) | Verlauf (dunkel) |
|---|---|
| ![Verlauf](docs/images/desktop-history-light.png) | ![Verlauf dunkel](docs/images/desktop-history-dark.png) |

| Einstellungen | Cloud im Browser | Cloud auf dem Telefon |
|---|---|---|
| ![Einstellungen](docs/images/desktop-settings-light.png) | ![Cloud](docs/images/cloud-history.png) | ![Telefon](docs/images/cloud-phone-light-history.png) |

## Bedienung per Tastatur

| Taste | Wirkung |
|---|---|
| `Strg + Win + V` / `⌃⌥V` | ThixClip öffnen / schließen (global, einstellbar) |
| tippen | suchen (Text, Links, erkannter Text in Bildern) |
| `↑` `↓` `Bild↑` `Bild↓`, `Strg/⌘ + Pos1/Ende` | Eintrag wählen |
| `Enter` / Doppelklick | in die vorherige App **einfügen** (oder nur kopieren, siehe Einstellungen) |
| `Alt + 1` … `Alt + 9` | den n-ten Eintrag sofort einfügen |
| `Strg/⌘ + C` | nur kopieren |
| `Strg/⌘ + D` | Favorit |
| `Strg/⌘ + P` | anheften |
| `Strg + Entf` / `⌘ + ⌫` | löschen |
| `Strg/⌘ + F` | Suche |
| `Strg/⌘ + 1 / 2 / 3` | Verlauf / Bilder / Favoriten |
| `Strg/⌘ + ,` | Einstellungen |
| `Esc` | Suche leeren, dann schließen (der Fokus geht zurück in die vorherige App) |

Die Versionsnummer unten links öffnet „Über ThixClip“.

## Installation

**Windows** – `ThixClipSetup.exe` aus dem [Release-Kanal](https://github.com/Real-TTX/ThixClip/releases/tag/release-channel)
starten. Installiert pro Benutzer nach `%LocalAppData%\Programs\ThixClip` (ohne Administratorrechte), mit Startmenü,
„Installierte Apps“, optional Autostart und Desktop-Verknüpfung. Deinstallieren über „Installierte Apps“.

**macOS** – `ThixClip-macOS-arm64.dmg` (Apple Silicon) bzw. `-x64.dmg` (Intel) öffnen und ThixClip in „Programme“ ziehen.
ThixClip lebt in der Menüleiste. Zum Einfügen in andere Apps fragt macOS einmal nach der Erlaubnis
„Bedienungshilfen“ (ohne sie wird nur kopiert). Die App ist (noch) ad hoc signiert: beim ersten Start Rechtsklick › Öffnen.

**Cloud** – auf einem Server mit Docker:

```bash
docker compose up -d
```

Dann `http://<server>:4950` öffnen: der Einrichtungsassistent legt den Administrator an. Für den Zugriff aus dem Internet
gehört ein Reverse-Proxy mit HTTPS davor. In ThixClip unter Einstellungen › Cloud Adresse, Benutzer und Passwort eingeben –
das Passwort wird nur für die Anmeldung verwendet, das Gerät bekommt ein eigenes Token (entziehbar unter Konto › Geräte).
Details: [docs/CLOUD.md](docs/CLOUD.md).

## Versionen, Kanäle, Updates

| Branch | Version | Desktop-Kanal | Docker-Image |
|---|---|---|---|
| `main` | `0.1.<build>-<datum>` (z. B. `0.1.42-20261115`) | `release-channel` + Release `v<version>` | `ghcr.io/real-ttx/thixclip:latest`, `:release`, `:<version>` |
| `dev` | `nightly-<build>-<datum>` | `nightly-channel` | `:nightly`, `:<version>` |
| sonst / lokal | `local-<datum>` | – (keine Updates) | – |

Jede installierte Version prüft beim Start und alle 6 Stunden ihren eigenen Kanal (abschaltbar); mit
„Updates automatisch installieren“ (Full-Auto-Update) wird ohne Rückfrage aktualisiert und neu gestartet.
Compose-Dateien: `docker-compose.yml` (Release), `docker-compose.dev.yml` (Nightly + pgAdmin),
`docker-compose.local.yml` (aus dem Quellcode gebaut, + pgAdmin, Postgres auf 4952).

## Für Administratoren

Richtlinien (Wert 1): Windows `HKLM\SOFTWARE\Policies\ThixClip`, macOS Konfigurationsprofil `info.schmoldt.thixclip`.

| Richtlinie | Wirkung |
|---|---|
| `LockConfiguration` | Einstellungen nur lesbar |
| `DisableCloud` | keine Verbindung zu ThixClip.Cloud |
| `DisableUpdates` | keine Update-Prüfung, keine Installation |

## Entwicklung

```bash
dotnet build ThixClip.slnx
dotnet test tests/ThixClip.Tests
dotnet test tests/ThixClip.Cloud.Tests
./scripts/redeploy.ps1
```

- `dotnet test tests/ThixClip.Tests` – Business-Layer, Sync gegen eine Cloud im Speicher und die Oberfläche headless
  gerendert (Tastatur-Abläufe; mit `THIXCLIP_SCREENSHOTS=<ordner>` auch Screenshots aller Seiten hell/dunkel).
  Mit `THIXCLIP_CLOUD_URL`, `THIXCLIP_CLOUD_USER`, `THIXCLIP_CLOUD_PASSWORD` läuft zusätzlich der Ende-zu-Ende-Test
  zweier Geräte gegen eine echte Cloud (z. B. den lokalen Stack).
- `THIXCLIP_DATA_DIR=<ordner>` startet die App mit eigenem Datenordner (eigene Instanz, berührt nichts Installiertes).
- Logo und Icons: `branding/` ([branding/README.md](branding/README.md)), neu rendern mit `cd tools && npm install && node render-icons.js`.
- Aufbau, Regeln und Protokoll: [CLAUDE.md](CLAUDE.md), [docs/CLOUD.md](docs/CLOUD.md).

© Matthias Schmoldt
