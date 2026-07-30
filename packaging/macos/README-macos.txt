TikMan for macOS
================

TikMan is not signed with an Apple Developer certificate. On first launch macOS therefore refuses it with

    "TikMan" is damaged and can't be opened. You should move it to the Bin.

Nothing is damaged. That is simply the message Gatekeeper shows for any app that carries the download
"quarantine" flag and has no signature it recognises — it does not tell "unsigned" apart from "corrupt".


Quick start (recommended)
-------------------------

Extract the archive, then run the included setup script once. In Terminal, from the folder you extracted:

    bash install.command

It removes the quarantine flag, offers to move TikMan.app to /Applications, and launches it. Starting a
script with `bash ...` is never blocked by Gatekeeper, even if the file itself was quarantined — so this
always works. (You can also double-click install.command in Finder; if that shows an "unidentified
developer" prompt, use the `bash install.command` line above instead.)


Doing it by hand
----------------

If you would rather run the steps yourself:

    tar -xzf TikMan-*-macos.tar.gz
    xattr -dr com.apple.quarantine TikMan.app        # <-- the step that actually unblocks it
    mv TikMan.app /Applications/
    open /Applications/TikMan.app

⚠️ Important: extracting with `tar` does NOT remove the quarantine flag on modern macOS. The system copies
the flag from the downloaded archive onto the extracted app (the "provenance" mechanism), so the app is
still blocked afterwards. This is true whether you extract with `tar` in Terminal or with Finder's Archive
Utility. The `xattr -dr com.apple.quarantine` line is what unblocks it — that one command is the whole
workaround, and it is exactly what install.command runs for you.


If the Mac freezes a few seconds after launch
---------------------------------------------

Start it with software rendering:

    /Applications/TikMan.app/Contents/MacOS/tikman --software-render

or set it for the whole bundle:

    TIKMAN_SOFTWARE_RENDER=1 open /Applications/TikMan.app

TikMan draws through the GPU (Metal). On a Mac whose graphics drivers are patched in rather than native —
an unsupported model running a newer macOS via OpenCore Legacy Patcher — a GPU render loop can take the
window server down with it, which looks like the whole system hanging rather than an app crashing.
Software rendering avoids the GPU path entirely. It costs a little scrolling smoothness and nothing else.

Try this before assuming the app is at fault: if software rendering is stable, the problem is the driver
stack, not TikMan.


Verify the download (optional)
------------------------------

A truncated download shows the same "damaged" message. Check the size first — a complete build is roughly
50 MB — and confirm the archive is readable end to end:

    ls -lh TikMan-*-macos.tar.gz
    tar -tzf TikMan-*-macos.tar.gz >/dev/null && echo "archive is intact"


Why it isn't signed
-------------------

Signing requires a paid Apple Developer account, and notarising uploads each build to Apple. Until that is
in place, removing the quarantine flag (install.command, or the xattr line above) is the whole workaround.
