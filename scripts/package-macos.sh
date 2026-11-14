#!/bin/sh
# Packs a published macOS build into ThixClip.app, a zip (for the update of installed versions) and a disk image
# (for the first installation: drag ThixClip into "Programme").
#
#   scripts/package-macos.sh <publish folder> <rid: osx-arm64|osx-x64> <version> <build number> <output folder>
#
# Runs on macOS (iconutil, codesign, ditto, hdiutil). The app lives in the menu bar (LSUIElement: no Dock icon).
# Without an Apple Developer ID the app is signed ad hoc (needed on Apple Silicon); Gatekeeper then asks once
# (right click > Öffnen, or System Settings > Privacy & Security). Pasting into other apps needs the accessibility
# permission, which macOS asks for the first time ThixClip pastes.
set -eu

publish="$1"
rid="$2"
version="$3"
build="$4"
out="$5"

root="$(cd "$(dirname "$0")/.." && pwd)"
arch="${rid#osx-}"
major=$(sed -n 's:.*<ThixClipMajor>\(.*\)</ThixClipMajor>.*:\1:p' "$root/Directory.Build.props")
minor=$(sed -n 's:.*<ThixClipMinor>\(.*\)</ThixClipMinor>.*:\1:p' "$root/Directory.Build.props")
work="$(mktemp -d)"
app="$work/ThixClip.app"

mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources" "$out"
cp -R "$publish/." "$app/Contents/MacOS/"
chmod +x "$app/Contents/MacOS/ThixClip"

# App icon from the rendered branding (tools/render-icons.js): the macOS variant keeps Apple's margin around the tile.
iconset="$work/ThixClip.iconset"
mkdir -p "$iconset"
for size in 16 32 128 256 512; do
  cp "$root/branding/png/thixclip-mac-${size}.png" "$iconset/icon_${size}x${size}.png"
  double=$((size * 2))
  cp "$root/branding/png/thixclip-mac-${double}.png" "$iconset/icon_${size}x${size}@2x.png"
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/ThixClip.icns"

cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleIdentifier</key>
  <string>info.schmoldt.thixclip</string>
  <key>CFBundleName</key>
  <string>ThixClip</string>
  <key>CFBundleDisplayName</key>
  <string>ThixClip</string>
  <key>CFBundleExecutable</key>
  <string>ThixClip</string>
  <key>CFBundleIconFile</key>
  <string>ThixClip</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>${major}.${minor}.${build}</string>
  <key>CFBundleVersion</key>
  <string>${build}</string>
  <key>CFBundleGetInfoString</key>
  <string>ThixClip ${version}</string>
  <key>NSHumanReadableCopyright</key>
  <string>© Matthias Schmoldt</string>
  <key>LSMinimumSystemVersion</key>
  <string>13.0</string>
  <key>LSApplicationCategoryType</key>
  <string>public.app-category.productivity</string>
  <key>LSUIElement</key>
  <true/>
  <key>NSHighResolutionCapable</key>
  <true/>
  <key>CFBundleDevelopmentRegion</key>
  <string>de</string>
  <key>NSAppleEventsUsageDescription</key>
  <string>ThixClip fügt den gewählten Eintrag in die App ein, aus der du es geöffnet hast.</string>
</dict>
</plist>
PLIST

# Ad-hoc signature: Apple Silicon runs only signed code. A Developer ID signature + notarization can replace it later.
codesign --force --deep --sign - "$app"
codesign --verify --deep --strict "$app"

# Update package (MacAppBundle unpacks it with ditto) and disk image for the first installation.
ditto -c -k --keepParent "$app" "$out/ThixClip-macOS-${arch}.zip"

dmg_root="$work/dmg"
mkdir -p "$dmg_root"
cp -R "$app" "$dmg_root/"
ln -s /Applications "$dmg_root/Programme"
hdiutil create -volname "ThixClip" -srcfolder "$dmg_root" -ov -format UDZO "$out/ThixClip-macOS-${arch}.dmg" >/dev/null

rm -rf "$work"
ls -l "$out"
