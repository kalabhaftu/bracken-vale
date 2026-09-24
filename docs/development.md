# Development

## Requirements

- Windows 10 version 1809 or later for running the app.
- .NET 10 SDK.
- Windows 10 SDK 10.0.26100 or later and Windows App SDK NuGet packages for WinUI builds.
- No Visual Studio IDE requirement. A Windows environment with the Windows SDK build targets is required; `dotnet` CLI alone on macOS/Linux cannot compile WinUI XAML.

## Commands

```powershell
dotnet restore BrackenVale.sln
dotnet test tests/BrackenVale.Tests/BrackenVale.Tests.csproj -c Release
dotnet build src/BrackenVale.App/BrackenVale.App.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64
```

For ARM64, replace `x64`/`win-x64` with `ARM64`/`win-arm64`. The automated Windows workflow builds both architectures and uploads per-architecture artifacts.

The core and its tests target .NET 10 without WinUI and can be tested on macOS or Linux. Native UI, audio devices, media keys, tray integration, installer behavior, MSIX installation and Windows 10/11 visual behavior must be checked on Windows hardware.

## Configuration and data

Application data lives under `%LOCALAPPDATA%\BrackenVale`; the SQLite database, artwork cache, recoverable tag backups and rotating logs are local. Logs are in `%LOCALAPPDATA%\BrackenVale\Logs` and can be opened from Settings. They contain error details and may include local file paths, so review them before sharing. The app has no server or public API. M3U8 is the playlist interchange format. LRCLIB searches send only title and artist after the user starts the search; GitHub is used only for the optional weekly release check.

## Release signing

Tagged releases require repository Actions secrets `BRACKENVALE_SIGNING_PFX_BASE64` and `BRACKENVALE_SIGNING_PFX_PASSWORD`. Create a PFX for a publisher whose subject exactly matches `CN=Bracken Vale`, protect its private key, and store only the base64 PFX and password as Actions secrets. Do not commit signing files. The release workflow fails before packaging if either secret is absent.

Windows signing can use a self-signed certificate for sideload testing, but Windows will not trust that certificate automatically. A publicly trusted code-signing certificate is required for ordinary signed installation; MSIX identity and publisher must remain aligned.
