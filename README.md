# Bracken Vale

Bracken Vale is a native Windows music library and player built with WinUI 3, C# and .NET. It uses Segoe UI and Fluent controls, with switchable system and album-art accent styles. Your library, playlists, ratings, lyrics, settings and playback session stay on your PC.

> **Development status:** this repository is under active development. The core library and automated tests run on macOS, but the WinUI application can only be built and visually/audio tested on Windows. Use GitHub Actions for the Windows build. No stable installer is available until Windows CI and hardware checks pass.

## Current capabilities

- Searchable SQLite library with album, artist, genre and folder groups; sort modes; favorites, five-star ratings, most/recently played views; and local M3U8 playlists.
- Background scan of fixed drives, folder selection, ignored paths, pause/cancel, and skipping system or linked directories.
- Playback through bundled LibVLC; an editable queue, shuffle, repeat, A–B repeat, crossfade, volume, EQ presets and saved 10-band EQ settings.
- Local tag editing with preview, explicit save, backup and restore; embedded/sidecar LRC lyrics and optional user-triggered LRCLIB search.
- System/light/dark themes, artwork or manual accent color, Windows media controls, optional tray behavior, and weekly GitHub release checks that never install updates.

The supported audio formats depend on the bundled LibVLC modules and the file container. See [the format matrix](docs/format-matrix.md); it is intentionally not a promise that every codec/tag combination has been verified.

## Get a Windows build

Open the repository's **Actions** tab and download the x64 or ARM64 CI artifact from a successful run. These are development builds. A tagged GitHub Release is the distribution path once signing credentials and Windows validation are configured.

## Build from the command line

Use Windows 10 1809 or later with the .NET 10 SDK and Windows 10 SDK 10.0.26100 or later. The Windows App SDK is restored by NuGet. Visual Studio IDE is not required; Windows SDK build tools are required for the native WinUI XAML build.

```powershell
dotnet restore BrackenVale.sln
dotnet test tests/BrackenVale.Tests/BrackenVale.Tests.csproj -c Release
dotnet build src/BrackenVale.App/BrackenVale.App.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64
```

For ARM64, use `-p:Platform=ARM64 -p:RuntimeIdentifier=win-arm64`. More packaging and development notes are in [docs/development.md](docs/development.md).

## Privacy and network use

There are no accounts, streaming, telemetry or cloud sync. The weekly update check can be disabled. LRCLIB is contacted only after you request a search, and only the track title and artist are sent. No audio files or file paths are sent to either service.

Crash and error logs are stored locally at `%LOCALAPPDATA%\BrackenVale\Logs`. Open the folder from Settings. Logs may contain local file paths and error details; review them before sharing.

## License

Bracken Vale source is MIT licensed. Third-party components and their notices are listed in [ThirdPartyNotices.md](ThirdPartyNotices.md).
