# TikMan packaging (Linux / macOS)

Headless TikMan (`TikMan.Host`, the `tikman-host` binary) is the same web dashboard the Windows GUI
hosts, running as its own process. These recipes wrap it so a non-technical user can double-click it.

## What the user gets

- **First launch generates a web login** (user `tikman` + a random password), saves it, writes it to
  `tikman-login.txt` in the settings folder, and opens the browser. HTTP Basic auth is mandatory, so
  this is what makes a double-click actually usable. Change it later with `--user/--pass`, or in the
  Windows GUI's Web settings. `--no-autologin` turns the generation off (for a server setup).
- Settings live in the platform's config home: `~/.config/TikMan` (Linux),
  `~/Library/Application Support/TikMan` (macOS), `%AppData%\TikMan` (Windows).

## Build inputs

Publish the self-contained host first (from the repo root, on any OS with the .NET 10 SDK):

```
dotnet publish src/TikMan.Host/TikMan.Host.csproj -c Release -r linux-x64  --self-contained true \
  -p:PublishSingleFile=true -o dist/host-linux
dotnet publish src/TikMan.Host/TikMan.Host.csproj -c Release -r osx-x64   --self-contained true \
  -p:PublishSingleFile=true -o dist/host-osx        # osx-arm64 for Apple-Silicon Macs
```

## Linux — AppImage

```
packaging/linux/build-appimage.sh dist/host-linux/tikman-host dist/appimage x86_64
```

Assembles an AppDir (`AppRun` + `tikman.desktop` + `tikman.png` + the binary) and, if `appimagetool`
is on `PATH`, packs `dist/appimage/TikMan-x86_64.AppImage`. The AppDir alone is already runnable
(`dist/appimage/TikMan.AppDir/AppRun`), so functionality can be tested without packing. The `.AppImage`
needs FUSE to run (every normal desktop distro has it; WSL1 does not, which is why it can be built but
not run there). End user: `chmod +x TikMan-x86_64.AppImage` then double-click (or `./TikMan-…AppImage`).

## macOS — .app bundle

```
packaging/macos/build-app.sh dist/host-osx/tikman-host dist/macos-app 2.2.2
```

Produces `dist/macos-app/TikMan.app`. **Run the script on a Unix host** (macOS/Linux/WSL), not plain
Windows: the inner binary needs its executable bit, which a Windows zip would drop. Ship it as a
`.tar.gz` (or zip made on Unix) so the bit survives. End user: unpack, then double-click `TikMan.app`.

⚠️ **Unsigned.** Without an Apple Developer signature Gatekeeper blocks the first launch — right-click →
Open, or `xattr -d com.apple.quarantine TikMan.app`. Signing/notarising needs a paid Apple Developer
account; wire it in later (`codesign` + `xcrun notarytool`).

## Stopping it

`tikman-host` runs until Ctrl+C (terminal) or until the process is quit (Activity Monitor / `kill`).
A proper tray/menu-bar quit is a later refinement.
