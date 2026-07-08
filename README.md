# TikMan

A small Windows tool (WPF, .NET 8) for keeping an eye on several MikroTik devices
(RouterOS v7) from one place: discovery, status/monitoring, logs, backups and updates —
without reimplementing RouterOS logic, just a convenient front-end over the REST API for
managing many devices at once.

> ⚠️ **Disclaimer**
>
> This tool is **vibe-coded** — largely generated with an AI assistant and only tested
> with a handful of devices, so it almost certainly still contains plenty of bugs. It is
> provided **without any warranty** (see [LICENSE](LICENSE), MIT) and used **entirely at
> your own risk**. The author accepts **no liability** for any damage, downtime,
> misconfiguration or data loss — in particular not for the "Install update" and "Backup"
> features, which reboot devices and export configurations. Test first, verify your
> backups, and mind the ordering when doing bulk updates.

## Screenshot

![TikMan main window with the network scan dialog open](screenshots/main_window.png)

*Main window (device list, tabs for logs/monitoring/update/details) with the MNDP
network-scan dialog on top.*

## Structure

```
src/
  TikMan.Core   Logic: REST client, MNDP/subnet discovery, storage (no UI dependencies)
  TikMan.App    WPF user interface (exe)
```

`Core` is deliberately kept UI-free — a later web server (ASP.NET) can reference the same
library.

## Features

- **Scan:** MNDP discovery (finds MikroTiks on the LAN via broadcast, port 5678) and a
  subnet scan (ping + port check, also finds other devices). Tick the discovered devices
  and add them with default credentials.
- **Monitoring:** auto-refresh (configurable interval) of CPU, RAM, uptime, version; a
  history chart per device.
- **Logs:** load a device's log on click, filterable (topic/text), newest first.
- **Updates:** update check across all devices; update channel switchable per device
  (stable / testing / long-term / development); an assistant installs sequentially in a
  chosen order and waits for each device to come back online.
- **Config backups (.rsc):** full text export of the configuration over HTTPS — per device
  (file dialog) or for all devices at once into a target folder. Sensitive values
  (passwords/keys) are hidden, as with the standard RouterOS export; restorable with
  `/import`. Files are named automatically: `<Identity/Board>_<IP>_<timestamp>.rsc`.

  Technical background: RouterOS does **not** return the `/export` output in the REST
  response (the command only writes to a file). The app therefore exports to a temporary
  device file, reads its content via the `contents` field, and deletes it again.

- **Full backup (.backup):** an exact binary copy including passwords (restorable only on
  the same model/version). The binary image **cannot** be fetched over the REST API, so the
  download uses one of two transports (selectable in settings):
  - **Web (WebFig):** MikroTik's proprietary, encrypted `jsproxy` protocol — *not yet
    implemented*, automatically falls back to SSH. (Reproducing the handshake is planned;
    it has to be developed and tested against a real device.)
  - **SSH (SCP):** over the factory-enabled, encrypted SSH service (port 22, via SSH.NET).
    The user needs the `ssh` policy. Active as the fallback by default.

- **Language:** English, German and Swiss German, switchable under **Settings** (⚙️). On
  first start the app follows the Windows display language: `de-CH` → Swiss German, other
  `de…` → German, everything else → English. The choice is stored in `devices.json` and
  takes effect immediately.

## Device requirements (RouterOS v7)

The app talks to the REST API (`https://<device>/rest/...`). For that, on each device:

1. **Enable www-ssl** (with a certificate; self-signed is fine):
   ```
   /certificate add name=local common-name=local key-usage=key-cert-sign,crl-sign
   /certificate sign local
   /certificate add name=https common-name=router
   /certificate sign https ca=local
   /ip service set www-ssl certificate=https disabled=no
   ```
   Alternatively, on a trusted LAN: enable `www` (HTTP, port 80) and untick HTTPS in the
   app.
2. **Create a dedicated API user** instead of using `admin`:
   ```
   /user group add name=monitor policy=read,write,reboot,rest-api,test
   /user add name=monitor group=monitor password=<strong-password>
   ```
   Permission overview:
   - Monitoring / logs / checking updates: `read, rest-api`
   - Switching the update channel & installing updates: additionally `write, reboot, test`
   - Config backup (.rsc): runs over HTTPS with `write` (for the temporary export file) —
     no FTP needed.

## Build & Run

```powershell
dotnet build                         # Debug build
dotnet run --project src\TikMan.App

# Release exe (single file, requires the .NET 8 Desktop Runtime):
dotnet publish src\TikMan.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
```

The finished `TikMan.exe` will then be in `dist\`.

## Security & data storage

- **Where are the credentials?** All settings including device passwords live in
  `%AppData%\TikMan\devices.json` — i.e. in the user profile, **not** in the program folder
  and **not** in the Git repository.
- **Passwords are encrypted.** They are encrypted with Windows DPAPI and bound to the
  Windows user account. The file cannot simply be moved to another PC/user; passwords must
  be re-entered there.
- **Nothing sensitive in the repo.** `.gitignore` excludes `devices.json`, exported backups
  (`*.rsc`, `*.backup`), the local `.claude/` folder and the `memory/` folder, so no
  internal network details or credentials are published by accident. **Before your first
  push, still run `git status` to confirm none of these files are listed.**
- **Config exports contain configuration.** A `.rsc` export hides passwords/keys; a binary
  `.backup` contains them. Neither belongs in a public repo.
- **Transport:** REST runs over HTTPS (self-signed certificates are accepted — common on a
  LAN). Choosing HTTP instead of HTTPS transmits credentials in clear text — only use it on
  a trusted network. Recommendation: a dedicated API user with minimal rights instead of
  `admin` (see above).

## Notes on bulk updates

Updates reboot the device. The assistant therefore works sequentially and in list order:
**edge devices first (APs, switches), the uplink router last** — otherwise you cut off the
connection to the remaining devices.

## Versioning & releases (maintainer notes)

1. Bump `<Version>` in `src/TikMan.App/TikMan.App.csproj` (e.g. `1.1.0`). This sets the
   exe's file/product version.
2. Build a self-contained single-file exe — runs on any Windows x64 **without** requiring
   a separate .NET install:
   ```powershell
   dotnet publish src\TikMan.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist\release
   ```
3. On GitHub → **Releases → Draft a new release**: create a tag `vX.Y.Z` (match `<Version>`),
   write short notes, and attach `dist\release\TikMan.exe`. Binaries live in Releases, not
   in git (`dist/` is git-ignored).
4. The exe is **unsigned**, so Windows SmartScreen shows an "unknown publisher" warning on
   first run — users click *More info → Run anyway*. (Code signing would remove this but
   needs a paid certificate.)
