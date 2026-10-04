# Development

## Requirements

- Windows 10 version 1809 or later for running the app.
- .NET 10 SDK.
- Windows 10 SDK 10.0.26100 or later and Windows App SDK NuGet packages for WinUI builds.
- No Visual Studio IDE requirement. A Windows environment with the Windows SDK build targets is required; `dotnet` CLI alone on macOS/Linux cannot compile WinUI XAML.
- Internet access to restore packages from NuGet. The app uses Windows App SDK, LibVLC and TagLib packages; a restore failure leaves the WinUI app unbuilt.

## Commands

```powershell
dotnet restore BrackenVale.sln
dotnet test tests/BrackenVale.Tests/BrackenVale.Tests.csproj -c Release
dotnet build src/BrackenVale.App/BrackenVale.App.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64
dotnet run --project src/BrackenVale.App/BrackenVale.App.csproj
```

`dotnet run` restores packages as needed, builds the app, and starts that build from the checkout. It does not produce the release ZIP, setup installer, or MSIX bundle. The first launch discovers mounted fixed drives when no library roots have been configured; use **Library folders** to choose locations explicitly.

For ARM64, replace `x64`/`win-x64` with `ARM64`/`win-arm64`. The automated Windows workflow builds both architectures and uploads per-architecture artifacts.

The core and its tests target .NET 10 without WinUI and can be tested on macOS or Linux. The Windows workflow builds and uploads a self-contained portable ZIP for x64 and ARM64. Native UI, audio devices, media keys, tray integration, installer behavior, MSIX installation and Windows 10/11 visual behavior must be checked on Windows hardware.

## Configuration and data

Application data lives under `%LOCALAPPDATA%\BrackenVale`; the SQLite database, artwork cache, recoverable tag backups and rotating logs are local. Logs are in `%LOCALAPPDATA%\BrackenVale\Logs` and can be opened from Settings. They contain error details and may include local file paths, so review them before sharing. The app has no server or public API. M3U8 is the playlist interchange format. LRCLIB searches send only title and artist after the user starts the search; GitHub is used only for the optional weekly release check.

## Release signing

See [the release process](release.md). Tagged releases use repository Actions secrets `BRACKENVALE_SIGNING_PFX_BASE64` and `BRACKENVALE_SIGNING_PFX_PASSWORD`. The workflow validates certificate trust, validity, Code Signing purpose, private key and the `CN=Bracken Vale` MSIX publisher, then signs and verifies a disposable file before packaging. It fails closed for stable releases; a preview may contain unsigned portable ZIPs if preflight does not pass. Secret presence alone does not mean the certificate is trusted or usable.

Do not commit signing material or print secret values. A self-signed certificate is suitable only for controlled sideload testing after manually trusting it. Ordinary signed distribution needs a publicly trusted code-signing certificate, and the MSIX identity/publisher must remain aligned. Signing does not guarantee that SmartScreen warnings disappear immediately or that every third-party antivirus gives the app a clean result.

## Search and memory limits

The UI reads library and playlist rows in 200-entry pages. SQLite FTS5's trigram tokenizer indexes substring searches of three or more characters; one- and two-character searches keep the existing `LIKE` behavior. Artwork thumbnails are decoded to display size and the in-memory cache is capped at an estimated 64 MB. The 100,000-track Windows x64 benchmark records the first-page search p95 against a 250 ms target.

Scanning uses per-run database generations instead of holding the whole library in a scan map. Unavailable or incomplete roots do not trigger stale-row deletion. Successful scan completion prunes only cached artwork paths no longer referenced by indexed tracks.
