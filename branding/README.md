# ThixClip – Logo

Ein Klemmbrett vor einem zweiten Blatt (der Verlauf) auf einer blauen Kachel. Akzentfarbe **#2F6FED**, dunkles Blau
**#163E9E**, Verlauf der Kachel **#5A9CFF → #1E5BE6**.

| Datei | Detailstufe | Einsatz |
|---|---|---|
| `logo-icon.svg` | voll (ab 48 px): Klemmbrett, Verlaufsblatt, drei Zeilen | App-Icon (Windows .ico ab 48 px, macOS .icns), Fenster, Über-Seite |
| `logo-small.svg` | reduziert (16–32 px): ein dickes Klemmbrett, zwei Zeilen, Kachel randlos | Favicon, kleine .ico-Größen, Taskleiste |
| `logo-tray.svg` | einfarbig, ohne Kachel | macOS-Menüleiste (Template-Bild, das System färbt es) |
| `logo-cloud.svg` | voll, randlose Kachel mit Wolke | Kachel für Startbildschirme (auch „maskable“): `png/thixclip-cloud-*.png` |
| `logo-full.svg` | Icon + Wortmarke „Thix**Clip**“ | README, Kopfzeilen, Über-Seiten |

`png/` enthält alle gerenderten Größen. Neu erzeugen (nach jeder Änderung an einer SVG):

```
cd tools && npm install && node render-icons.js
```

Das Skript rendert mit dem installierten Microsoft Edge (headless) und schreibt zusätzlich
`src/ThixClip/Assets/*` (thixclip.ico, Tray-Icons, Logos).
Die macOS-Varianten `png/thixclip-mac-*.png` halten Apples Rand um die Kachel ein; `scripts/package-macos.sh` baut daraus das `.icns`.
