# Cutting a release

Notes to self. Nothing here is needed to *use* TikMan — that's the [README](README.md).

1. Bump `<Version>` in the app projects so they match: `src/TikMan.App.Avalonia`, `src/TikMan.App`,
   `src/TikMan.Host`.
2. Publish the two Windows variants (`win-x64` / `win-arm64`), self-contained single-file:

   ```powershell
   dotnet publish src\TikMan.App.Avalonia -c Release -r win-x64 --self-contained true `
     -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
     -p:EnableCompressionInSingleFile=true -o dist\release
   ```

   Linux/macOS are packaged from `packaging/` (AppImage, `.app`/tar.gz).

   No `-fdd` (framework-dependent) builds any more — a self-contained exe just works, runtime or not,
   and it isn't worth the extra assets. The auto-updater **migrates an existing `-fdd` install to the
   self-contained exe** (it falls back to the self-contained asset of the same arch when no `-fdd`
   asset is present), so dropping them doesn't strand anyone. Reintroduce later if it's ever needed.
3. Name the assets **exactly** — the in-app auto-updater matches on the name; get it wrong and
   existing installs stop finding updates:
   - Windows: `TikMan-<version>-win-x64.exe` / `-win-arm64.exe`
   - Linux:   `TikMan-<version>-linux-x86_64.AppImage` / `-linux-aarch64.AppImage`
   - macOS:   `TikMan-<version>-macos.tar.gz`
4. GitHub → **Releases → Draft a new release**: tag `vX.Y.Z` (matching `<Version>`), write the notes,
   attach the assets. Binaries live in Releases, never in git.
