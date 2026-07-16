# Cutting a release

Notes to self. Nothing here is needed to *use* TikMan — that's the [README](README.md).

1. Bump `<Version>` in `src/TikMan.App/TikMan.App.csproj`.
2. Publish all four variants (`win-x64` / `win-arm64` × self-contained / `-fdd`):

   ```powershell
   dotnet publish src\TikMan.App\TikMan.App.csproj -c Release -r win-x64 --self-contained true `
     -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
     -p:EnableCompressionInSingleFile=true -o dist\release
   ```

   `EnableCompressionInSingleFile` only applies to self-contained builds — passing it to an `-fdd`
   publish fails.
3. Name the assets **exactly** `TikMan-<version>-win-<arch>[-fdd].exe`. ⚠️ The in-app auto-updater
   matches on that name: get it wrong and existing installations stop finding updates.
4. GitHub → **Releases → Draft a new release**: tag `vX.Y.Z` (matching `<Version>`), write the
   notes, attach the four exes. Binaries live in Releases, never in git.
