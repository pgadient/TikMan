#!/usr/bin/env bash
# Assembles TikMan.app from a published osx single-file binary (osx-x64 or osx-arm64).
# A .app is just a directory tree; the only thing that must be set on a non-mac build host is the
# executable bit on the inner binary – which is why this runs under bash (WSL/Linux/macOS), not on
# plain Windows where zipping would drop the +x and the bundle wouldn't launch.
#
# Usage: build-app.sh <path-to-tikman-host-binary> <output-dir> [version]
set -euo pipefail

BIN="${1:?path to the published tikman-host binary}"
OUT="${2:?output directory}"
VER="${3:-0.0.0}"

APP="$OUT/TikMan.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

cp "$BIN" "$APP/Contents/MacOS/tikman-host"
chmod +x "$APP/Contents/MacOS/tikman-host"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
	<key>CFBundleName</key>              <string>TikMan</string>
	<key>CFBundleDisplayName</key>       <string>TikMan</string>
	<key>CFBundleIdentifier</key>        <string>org.gadient.tikman</string>
	<key>CFBundleVersion</key>           <string>$VER</string>
	<key>CFBundleShortVersionString</key><string>$VER</string>
	<key>CFBundleExecutable</key>        <string>tikman-host</string>
	<key>CFBundlePackageType</key>       <string>APPL</string>
	<key>LSMinimumSystemVersion</key>    <string>10.15</string>
	<key>NSHighResolutionCapable</key>   <true/>
	<!-- A network tool with no signed entitlements: keep it a normal foreground app so the browser it
	     opens is obviously "the UI", and it can be quit from Activity Monitor. -->
</dict>
</plist>
PLIST

echo "built $APP  (version $VER)"
