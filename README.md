<p align="center"><img src="branding/png/thixclip-full.png" alt="ThixClip" width="420"></p>

# ThixClip

**Der Zwischenablage-Verlauf für Windows und macOS** – schnell, komplett per Tastatur bedienbar und auf Wunsch über
**MatCloud** auf allen deinen Computern gleich (auch im Browser).

- **Tastenkürzel**: `Strg + Umschalt + V` (Windows) bzw. `⌃⌥V` (macOS) öffnet ThixClip aus jeder App – es hat Vorrang vor
  gleichen Kürzeln anderer Programme und ist in den Einstellungen änderbar.
- **Sofort da**: das Fenster wird beim Start einmal gebaut und danach nur noch ein- und ausgeblendet; die Suche läuft
  im Speicher bei jedem Tastendruck.
- **Texte, Links und Bilder** – mit **Texterkennung (OCR)**: die Suche findet auch Text in Screenshots
  (Windows-OCR bzw. Apple Vision, offline auf dem Gerät).
- **Favoriten und Anheften**: bleiben dauerhaft, alles andere nach der **Speicherdauer** (1 Tag … unbegrenzt).
- **Verlauf löschen wie im Browser**: letzte Stunde, 24 Stunden, 7 Tage, 4 Wochen oder alles – mit der Anzahl je
  Zeitraum vorab; Favoriten und Angeheftetes bleiben, mit der Cloud verschwindet es auf allen Geräten.
- **Einklappbares Menü**: die Seitenleiste lässt sich auf ihre Symbole einklappen und bleibt so.
- **Aufzeichnung pausieren**: der Knopf oben im Fenster (oder `Strg/⌘ + Umschalt + P`, das Tray-Menü) fragt nach der Dauer –
  15 / 30 Minuten, 1 Stunde, bis zum Neustart oder bis du fortsetzt. Während der Pause trägt das Tray-Symbol einen
  gelben Punkt mit Pause-Zeichen (sonst grün: läuft) und oben steht „Fortsetzen“.
- **Ansehen**: ein Eintrag öffnet sich in ThixClip selbst – ein Bild groß (Leertaste: Originalgröße), ein Text oder Link
  ganz und markierbar; von dort einfügen, kopieren oder einen Link im Browser öffnen. Mit `Strg/⌘ + O`, `Strg/⌘`+Klick,
  einem Klick auf das Bild oder dem Knopf in der Vorschau.
- **Abstürze und Hänger werden sichtbar**: ein unerwarteter Fehler erscheint sofort mit seiner Meldung, ein Hänger wird mit
  Bericht (unter Windows mit Speicherabbild) im Protokollordner festgehalten; beim nächsten Start zeigt ThixClip den Bericht.
- **Vertrauliches bleibt draußen**: was Passwort-Manager als vertraulich markieren, wird nie aufgezeichnet.
- **Im Look der Mat-Familie**: Farben, Symbole, Titelleiste, Menü, Tray, Benachrichtigungen und Setup kommen aus
  **MatDesktop**, dem Desktop-Toolkit von MatDesign (hell, dunkel oder wie das System).
- **Tray / Menüleiste**, **Autostart**, **Setup** (Windows) bzw. **App-Bundle + DMG** (macOS), **OTA-Updates** mit
  optionalem **Full-Auto-Update**.
- **MatCloud** (die Cloud der Thix-Apps, eigenes Repository, Docker, Port 4950): Einträge, Favoriten und Löschungen werden
  zwischen allen Geräten abgeglichen, im Browser liest, sucht, kopiert und ergänzt man den Verlauf auch unterwegs.
  Angemeldet wird **im Browser** (jedes Konto der MatCloud, auch eines über MatIdentity ohne Passwort): ThixClip zeigt einen
  Code, der Browser öffnet sich, dort bestätigen – fertig. Alternativ mit Benutzername und Passwort.

| Verlauf (hell) | Verlauf (dunkel) |
|---|---|
| ![Verlauf](docs/images/main-history-light.png) | ![Verlauf dunkel](docs/images/main-history-dark.png) |

| Einstellungen | Cloud: im Browser anmelden | Cloud: Code bestätigen |
|---|---|---|
| ![Einstellungen](docs/images/main-settings-light.png) | ![Cloud](docs/images/main-cloud-light.png) | ![Code](docs/images/main-cloud-code-light.png) |

## Bedienung per Tastatur

| Taste | Wirkung |
|---|---|
| `Strg + Umschalt + V` / `⌃⌥V` | ThixClip öffnen / schließen (global, einstellbar) |
| tippen | suchen (Text, Links, erkannter Text in Bildern) |
| `↑` `↓` `Bild↑` `Bild↓`, `Strg/⌘ + Pos1/Ende` | Eintrag wählen |
| `Enter` / Doppelklick | in die vorherige App **einfügen** (oder nur kopieren, siehe Einstellungen) |
| `Alt + 1` … `Alt + 9` | den n-ten Eintrag sofort einfügen |
| `Strg/⌘ + C` | kopieren und ThixClip schließen (abschaltbar: Einstellungen › „Nach dem Kopieren schließen“) |
| `Strg/⌘ + D` | Favorit |
| `Strg/⌘ + P` | anheften |
| `Entf` (Suche leer), `Strg + Entf` / `⌘ + ⌫` | löschen – der nächste Eintrag rückt nach und ist gewählt |
| `Strg/⌘ + O`, `Strg/⌘` + Klick | ansehen (Bild, Text, Link) – dort `Enter` einfügen, `Strg/⌘ + C` kopieren, `Leertaste` Originalgröße, `Esc` schließen |
| `Strg + Umschalt + Entf` / `⌘ + ⇧ + ⌫` | **Verlauf löschen**: Zeitraum mit `↑` `↓` wählen, `Enter` löscht, `Esc` bricht ab |
| `Strg/⌘ + F` | Suche |
| `Strg/⌘ + 1 / 2 / 3` | Verlauf / Bilder / Favoriten |
| `Strg/⌘ + ,` | Einstellungen |
| `Strg/⌘ + B` | Menü ein- / ausklappen |
| `Strg/⌘ + Umschalt + P` | Aufzeichnung pausieren (mit Dauer) / fortsetzen |
| `Esc` | Suche leeren, dann schließen (der Fokus geht zurück in die vorherige App) |

Die Version unten links („Version 1.0.0 · release“) öffnet „Über ThixClip“ (eingeklappt: das ⓘ-Symbol), der Knopf daneben klappt
das Menü ein. Liegt ein Update bereit, steht „Update auf …“ darüber; „Über ThixClip“ führt mit „Update installieren …“ zu
Einstellungen › Updates.

| Menü eingeklappt | Verlauf löschen |
|---|---|
| ![Menü eingeklappt](docs/images/main-folded-light.png) | ![Verlauf löschen](docs/images/dialog-clear-history-light.png) |

| Aufzeichnung pausiert | Pause mit Dauer | Bild ansehen |
|---|---|---|
| ![Pausiert](docs/images/main-paused-light.png) | ![Pause](docs/images/dialog-pause-light.png) | ![Ansehen](docs/images/dialog-viewer-light.png) |

## Installation

**Windows** – `ThixClipSetup.exe` aus dem [Release-Kanal](https://github.com/Real-TTX/ThixClip/releases/tag/release-channel)
starten (rund 38 MB). Installiert pro Benutzer nach `%LocalAppData%\Programs\ThixClip` (ohne Administratorrechte), mit
Startmenü, „Installierte Apps“, optional Autostart, Desktop-Verknüpfung und automatischen Updates (vorausgewählt).
Deinstallieren über „Installierte Apps“.

**macOS** – `ThixClip-macOS-arm64.dmg` (Apple Silicon) bzw. `-x64.dmg` (Intel) öffnen und ThixClip in „Programme“ ziehen.
ThixClip lebt in der Menüleiste. Zum Einfügen in andere Apps fragt macOS einmal nach der Erlaubnis
„Bedienungshilfen“ (ohne sie wird nur kopiert). Die App ist (noch) ad hoc signiert: beim ersten Start Rechtsklick › Öffnen.

**Cloud** – die Cloud ist **MatCloud**, die gemeinsame Cloud aller Thix-Apps (eigenes Repository `MatCloud`, Docker,
Port 4950; Installation dort beschrieben). In ThixClip unter Einstellungen › Cloud die Adresse eingeben und
**„Im Browser anmelden“** wählen: ThixClip zeigt einen Code und öffnet die Seite der MatCloud; dort anmelden (mit Passwort
oder über MatIdentity), prüfen, dass derselbe Code dasteht, und bestätigen. Für lokale Konten geht es auch
**„Mit Benutzername und Passwort“** – das Passwort wird nur für die Anmeldung verwendet. Das Gerät bekommt so oder so ein
eigenes Token (in der MatCloud unter den Geräten entziehbar). Die Speicherdauer gilt für das ganze Konto.

## Versionen, Kanäle, Updates

| Branch | Version | Desktop-Kanal |
|---|---|---|
| `main` | `0.1.<build>-<datum>` (z. B. `0.1.42-20261115`) | `release-channel` + Release `v<version>` |
| `dev` | `nightly-<build>-<datum>` | `nightly-channel` |
| sonst / lokal | `local-<datum>` | – (keine Updates) |

Jede installierte Version prüft beim Start und alle 6 Stunden ihren eigenen Kanal (abschaltbar); mit
„Updates automatisch installieren“ (Full-Auto-Update, bei neuen Installationen an) wird ohne Rückfrage aktualisiert und neu gestartet.

## Für Administratoren

Richtlinien (Wert 1): Windows `HKLM\SOFTWARE\Policies\ThixClip`, macOS Konfigurationsprofil `info.schmoldt.thixclip`.

| Richtlinie | Wirkung |
|---|---|
| `LockConfiguration` | Einstellungen nur lesbar |
| `DisableCloud` | keine Verbindung zur MatCloud |
| `DisableUpdates` | keine Update-Prüfung, keine Installation |

## Entwicklung

```bash
dotnet build ThixClip.slnx
dotnet test tests/ThixClip.Tests
../MatCloud/scripts/redeploy.ps1   # die Cloud lokal (Nachbar-Repository MatCloud, http://localhost:4950)
```

- `dotnet test tests/ThixClip.Tests` – Business-Layer, Sync gegen eine MatCloud im Speicher (`FakeCloud`, auch die Anmeldung
  im Browser) und die Oberfläche headless gerendert (Tastatur-Abläufe; mit `THIXCLIP_SCREENSHOTS=<ordner>` auch Screenshots
  aller Seiten hell/dunkel). Mit `THIXCLIP_CLOUD_URL`, `THIXCLIP_CLOUD_USER`, `THIXCLIP_CLOUD_PASSWORD` läuft zusätzlich der
  Ende-zu-Ende-Test zweier Geräte gegen eine echte MatCloud (z. B. den lokalen Stack, ein Konto mit Passwort).
- Die Cloud ist **MatCloud** im Nachbar-Repository `../MatCloud` (ThixClips Serverseite ist dort das Modul `MatCloud.Apps.Clip`).
  Ihr Vertrag (Routen, DTOs, `MatCloudClient`) liegt als **Kopie** in `src/ThixClip.Core/MatCloud/`: nie hier ändern, sondern in
  MatCloud und neu kopieren mit `node ../MatCloud/tools/contract-copy.js src/ThixClip.Core`; ein Test prüft die Kopie
  gegen `matcloud.version`.
- `ThixClip.exe --self-test <bericht.txt>` prüft das veröffentlichte (getrimmte) Programm selbst – JSON, SQLite, Skia, OCR,
  Verknüpfungen, alle Fenster – in einem eigenen Temp-Ordner; die CI führt das bei jedem Windows-Build aus.
- `dotnet run --project src/ThixClip -- --snapshot docs/images` (oder `ThixClip.exe --snapshot <ordner>`) zeichnet die Fenster
  ohne Bildschirm als PNG (hell und dunkel, 1x, natürliche Größe: `setup-*`, `main-*`, `dialog-*`, `toast-*`, `tray-icon-*`) –
  mit Beispielverlauf in einem Temp-Ordner, fester Uhrzeit, Version 1.0.0 und dem Beispielrechner ANNA-PC; alle Bilder in
  `docs/images` (und die in MatDesign) kommen daher, nie vom echten Rechner.
- `src/ThixClip/MatDesktop/` ist eine Kopie von MatDesktop aus MatDesign: nie hier ändern, sondern in MatDesign und neu
  kopieren (`node tools/matdesktop-copy.js <pfad>/ThixClip/src/ThixClip` im MatDesign-Repository); ein Test prüft die Kopie.
- `THIXCLIP_DATA_DIR=<ordner>` startet die App mit eigenem Datenordner (eigene Instanz, berührt nichts Installiertes).
- Logo und Icons: `branding/` ([branding/README.md](branding/README.md)), neu rendern mit `cd tools && npm install && node render-icons.js`.
- Aufbau und Regeln: [CLAUDE.md](CLAUDE.md).

© Matthias Schmoldt
