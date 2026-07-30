#!/bin/bash
#
# TikMan macOS setup — run this once after downloading.
#
# Why it exists: TikMan is not signed with an Apple Developer certificate, so macOS tags it with a
# "quarantine" flag on download and then refuses to open it with
#
#     "TikMan" is damaged and can't be opened. You should move it to the Bin.
#
# Nothing is damaged — that is just the message Gatekeeper shows for any unsigned, quarantined app.
# This script removes that flag (the `xattr` step you would otherwise type by hand), optionally moves
# the app to /Applications, and launches it.
#
# ⚠️ If DOUBLE-CLICKING this file is itself blocked ("unidentified developer"), run it from Terminal
#    instead — a script started that way is never Gatekeeper-blocked, even when quarantined:
#
#        bash install.command
#
# Deliberately no `set -e`: one non-fatal step (e.g. a declined /Applications copy) must not abort the
# whole setup.

cd "$(dirname "$0")" || { echo "Could not enter the script's folder."; exit 1; }

APP="TikMan.app"
if [ ! -d "$APP" ]; then
    echo "X  $APP is not in this folder:"
    echo "   $(pwd)"
    echo "   Extract the whole archive first, then run this script from inside it."
    exit 1
fi

echo "->  Removing the quarantine flag from $APP ..."
# -r: recurse into the bundle; the flag sits on individual files, not just the top folder.
xattr -dr com.apple.quarantine "$APP" 2>/dev/null

TARGET="$(pwd)/$APP"

# Offer to install into /Applications — but only when we can actually ask (double-clicked, or an
# interactive terminal). Piped / non-interactive runs just de-quarantine and launch in place.
if [ -t 0 ]; then
    printf "->  Move TikMan.app to /Applications? [Y/n] "
    read -r answer
    case "$answer" in
        [Nn]*)
            echo "    Keeping it here."
            ;;
        *)
            echo "    Copying to /Applications ..."
            rm -rf "/Applications/$APP"
            if cp -R "$APP" /Applications/ 2>/dev/null; then
                xattr -dr com.apple.quarantine "/Applications/$APP" 2>/dev/null
                TARGET="/Applications/$APP"
            else
                echo "    !  Could not copy to /Applications (permissions?) — launching in place instead."
            fi
            ;;
    esac
fi

echo "->  Launching TikMan ..."
if open "$TARGET"; then
    echo "OK  Done. If the Mac freezes a few seconds after launch, see README-macos.txt"
    echo "    (software-rendering fallback)."
else
    echo "!  'open' failed. Try running it directly:"
    echo "     open \"$TARGET\""
    exit 1
fi
