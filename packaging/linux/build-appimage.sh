#!/usr/bin/env bash
# Builds a Linux AppImage from a published linux single-file binary.
#
# Assembles an AppDir (AppRun + .desktop + icon + the binary) and, if appimagetool is available,
# packs it into a single-file TikMan-<arch>.AppImage. The AppDir on its own is already runnable
# (./AppDir/AppRun), so the app can be tested without producing the .AppImage at all.
#
# Usage: build-appimage.sh <path-to-tikman-host-binary> <output-dir> [arch]
#   arch defaults to x86_64. Needs: appimagetool on PATH (or $APPIMAGETOOL) to produce the .AppImage;
#   without it the script still leaves a ready-to-run AppDir and says so.
set -euo pipefail

BIN="${1:?path to the published tikman-host binary}"
OUT="${2:?output directory}"
ARCH="${3:-x86_64}"
HERE="$(dirname "$(readlink -f "$0")")"

APPDIR="$OUT/TikMan.AppDir"
rm -rf "$APPDIR"
mkdir -p "$APPDIR/usr/bin"

cp "$BIN" "$APPDIR/usr/bin/tikman-host"
chmod +x "$APPDIR/usr/bin/tikman-host"
cp "$HERE/AppRun" "$APPDIR/AppRun";               chmod +x "$APPDIR/AppRun"
cp "$HERE/tikman.desktop" "$APPDIR/tikman.desktop"
# Icon: top-level tikman.png is what appimagetool picks up (named after the desktop file's Icon=).
if [ -f "$HERE/tikman.png" ]; then cp "$HERE/tikman.png" "$APPDIR/tikman.png"; fi
echo "AppDir ready: $APPDIR  (run it with: $APPDIR/AppRun)"

TOOL="${APPIMAGETOOL:-appimagetool}"
if command -v "$TOOL" >/dev/null 2>&1; then
    ARCH="$ARCH" "$TOOL" "$APPDIR" "$OUT/TikMan-$ARCH.AppImage"
    echo "built $OUT/TikMan-$ARCH.AppImage"
else
    echo "appimagetool not found — AppDir is complete; run appimagetool on any Linux box to pack it:" >&2
    echo "  appimagetool $APPDIR TikMan-$ARCH.AppImage" >&2
fi
