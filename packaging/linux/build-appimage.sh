#!/usr/bin/env bash
# Builds a Linux AppImage from a published linux single-file binary.
#
# Assembles an AppDir (AppRun + .desktop + icon + the binary) and, if appimagetool is available,
# packs it into a single-file TikMan-<arch>.AppImage. The AppDir on its own is already runnable
# (./AppDir/AppRun), so the app can be tested without producing the .AppImage at all.
#
# Usage: build-appimage.sh <binary> <output-dir> [arch] [exec-name] [terminal]
#   arch     defaults to x86_64
#   exec-name defaults to tikman-host (use "tikman" for the Avalonia desktop client)
#   terminal  defaults to true — the headless host prints its generated web login, so a file manager
#             that honours the flag shows it. A GUI client must pass false, otherwise some desktops
#             open a pointless terminal window alongside the window.
# Needs appimagetool on PATH (or $APPIMAGETOOL) to produce the .AppImage; without it the script still
# leaves a ready-to-run AppDir and says so.
set -euo pipefail

BIN="${1:?path to the published binary}"
OUT="${2:?output directory}"
ARCH="${3:-x86_64}"
EXEC="${4:-tikman-host}"
TERMINAL="${5:-true}"
HERE="$(dirname "$(readlink -f "$0")")"

APPDIR="$OUT/TikMan.AppDir"
rm -rf "$APPDIR"
mkdir -p "$APPDIR/usr/bin"

cp "$BIN" "$APPDIR/usr/bin/$EXEC"
chmod +x "$APPDIR/usr/bin/$EXEC"
# AppRun and the .desktop both have to name the same executable.
sed "s|@EXEC@|$EXEC|g" "$HERE/AppRun" > "$APPDIR/AppRun"; chmod +x "$APPDIR/AppRun"
sed -e "s|^Exec=.*|Exec=$EXEC|" -e "s|^Terminal=.*|Terminal=$TERMINAL|" \
    "$HERE/tikman.desktop" > "$APPDIR/tikman.desktop"
# Icon: top-level tikman.png is what appimagetool picks up (named after the desktop file's Icon=).
# Also placed in the hicolor theme inside the AppDir: that is where a desktop expects to find it, and the
# client's own desktop integration (DesktopIntegration.cs) copies it out from either location.
if [ -f "$HERE/tikman.png" ]; then
    cp "$HERE/tikman.png" "$APPDIR/tikman.png"
    mkdir -p "$APPDIR/usr/share/icons/hicolor/256x256/apps"
    cp "$HERE/tikman.png" "$APPDIR/usr/share/icons/hicolor/256x256/apps/tikman.png"
fi
# The .desktop also belongs in the standard location, so desktops that inspect the AppDir find it.
mkdir -p "$APPDIR/usr/share/applications"
cp "$APPDIR/tikman.desktop" "$APPDIR/usr/share/applications/tikman.desktop"
echo "AppDir ready: $APPDIR  (run it with: $APPDIR/AppRun)"

TOOL="${APPIMAGETOOL:-appimagetool}"
if command -v "$TOOL" >/dev/null 2>&1; then
    ARCH="$ARCH" "$TOOL" "$APPDIR" "$OUT/TikMan-$ARCH.AppImage"
    echo "built $OUT/TikMan-$ARCH.AppImage"
else
    echo "appimagetool not found — AppDir is complete; run appimagetool on any Linux box to pack it:" >&2
    echo "  appimagetool $APPDIR TikMan-$ARCH.AppImage" >&2
fi
