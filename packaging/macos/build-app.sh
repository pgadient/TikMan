#!/usr/bin/env bash
# Assembles TikMan.app from a published osx single-file binary (osx-x64 or osx-arm64).
# A .app is just a directory tree; the only thing that must be set on a non-mac build host is the
# executable bit on the inner binary – which is why this runs under bash (WSL/Linux/macOS), not on
# plain Windows where zipping would drop the +x and the bundle wouldn't launch.
#
# Usage: build-app.sh <binary> <output-dir> [version] [exec-name]
#   exec-name defaults to tikman-host (use "tikman" for the Avalonia desktop client)
set -euo pipefail

BIN="${1:?path to the published binary}"
OUT="${2:?output directory}"
VER="${3:-0.0.0}"
EXEC="${4:-tikman-host}"
HERE="$(dirname "$(readlink -f "$0")")"

APP="$OUT/TikMan.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

cp "$BIN" "$APP/Contents/MacOS/$EXEC"
chmod +x "$APP/Contents/MacOS/$EXEC"

# Icon: macOS wants an .icns in Resources plus CFBundleIconFile below. The .icns is generated from the
# 512px PNG by packaging/macos/make-icns.ps1 and committed, so this script needs no image tooling on the
# build host. Without it the Finder shows a blank document icon.
ICON=""
if [ -f "$HERE/tikman.icns" ]; then
    cp "$HERE/tikman.icns" "$APP/Contents/Resources/tikman.icns"
    ICON="	<key>CFBundleIconFile</key>          <string>tikman</string>"
else
    echo "warning: $HERE/tikman.icns missing – the bundle will have no icon" >&2
fi

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
	<key>CFBundleExecutable</key>        <string>$EXEC</string>
$ICON
	<key>CFBundlePackageType</key>       <string>APPL</string>
	<key>LSMinimumSystemVersion</key>    <string>10.15</string>
	<key>NSHighResolutionCapable</key>   <true/>
	<!-- A network tool with no signed entitlements: keep it a normal foreground app so the browser it
	     opens is obviously "the UI", and it can be quit from Activity Monitor. -->
</dict>
</plist>
PLIST

# End-user helpers, shipped ALONGSIDE the app (not inside the bundle): the setup script that clears the
# Gatekeeper quarantine flag an unsigned app carries, and the README that explains it. Without these the
# user hits "TikMan is damaged and can't be opened" and has no in-archive hint how to get past it.
if [ -f "$HERE/install.command" ]; then
    cp "$HERE/install.command" "$OUT/install.command"
    chmod +x "$OUT/install.command"
else
    echo "warning: $HERE/install.command missing – archive will have no setup helper" >&2
fi
if [ -f "$HERE/README-macos.txt" ]; then
    cp "$HERE/README-macos.txt" "$OUT/README-macos.txt"
else
    echo "warning: $HERE/README-macos.txt missing" >&2
fi

echo "built $APP  (version $VER)"

# Pack a ready-to-ship tarball: TikMan.app + the two helpers, with executable bits preserved – which is
# the whole reason this must run on Unix rather than a Windows zip. The file list is explicit, so the
# tarball never contains itself even though it is written into the same directory.
TARBALL="TikMan-$VER-macos.tar.gz"
PACK="TikMan.app"
[ -f "$OUT/install.command" ]  && PACK="$PACK install.command"
[ -f "$OUT/README-macos.txt" ] && PACK="$PACK README-macos.txt"
rm -f "$OUT/$TARBALL"
( cd "$OUT" && tar -czf "$TARBALL" $PACK )
echo "packed  $OUT/$TARBALL"
