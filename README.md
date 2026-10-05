# Bracken Vale

Bracken Vale is a native Windows music library and player built with WinUI 3, C# and .NET. It uses Segoe UI and Fluent controls, with switchable system and album-art accent styles. Your library, playlists, ratings, lyrics, settings and playback session stay on your PC.

> **Development status:** this repository is under active development. The core library and automated tests run cross-platform; the WinUI application must be built and visually/audio tested on Windows. The release workflow supports unsigned preview ZIPs and blocks stable releases until signing preflight passes. See [project status](docs/project-status.md) for verified results and [known limitations](docs/known-limitations.md).

## Current capabilities

- Searchable SQLite library with album, artist, genre and folder groups; sort modes; favorites, five-star ratings, most/recently played views; and local M3U8 playlists.
- Reorderable and hideable library navigation, with adjustable navigation and browse pane widths.
- Background scan of fixed drives, folder selection, ignored paths, pause/cancel, and skipping system or linked directories.
- Playback through bundled LibVLC; an editable queue, shuffle, repeat, A–B repeat, crossfade, volume, EQ presets and saved 10-band EQ settings.
- Choose a connected Windows audio output, including Bluetooth headphones and speakers; output selection is saved and can be refreshed in Settings.
- Choose Mica, Desktop Acrylic glass, or an opaque window surface; set page transitions to subtle, expressive, or off. Motion follows Windows accessibility preferences and never animates individual library rows.
- Paused-session restore for the track, position, queue, shuffle, repeat mode and A–B marks.
- Local tag editing with preview, explicit save, backup and restore; standard and format-specific text fields, embedded/sidecar LRC lyrics and optional user-triggered LRCLIB search.
- System/light/dark themes, artwork or manual accent color, Windows media controls, optional tray behavior, and weekly GitHub release checks that never install updates.

The supported audio formats depend on the bundled LibVLC modules and the file container. See [the format matrix](docs/format-matrix.md); it is intentionally not a promise that every codec/tag combination has been verified.

## Get a Windows build

Download the latest x64 or ARM64 portable ZIP from [GitHub Releases](https://github.com/kalabhaftu/bracken-vale/releases). Extract it and start `BrackenVale.exe`. These self-contained builds do not need a separate .NET or codec installation. Preview ZIPs may be unsigned when signing preflight has not passed, and Windows may show a security warning. Signed setup installers and MSIX releases require a trusted certificate and Windows installation checks.

## Build from the command line

Use Windows 10 1809 or later with the .NET 10 SDK and Windows 10 SDK 10.0.26100 or later. The Windows App SDK is restored by NuGet. Visual Studio IDE is not required; Windows SDK build tools are required for the native WinUI XAML build.

```powershell
dotnet restore BrackenVale.sln
dotnet test tests/BrackenVale.Tests/BrackenVale.Tests.csproj -c Release
dotnet build src/BrackenVale.App/BrackenVale.App.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64
dotnet run --project src/BrackenVale.App/BrackenVale.App.csproj
```

`dotnet run` starts the app built from this checkout; it does not make release packages. For ARM64 builds, use `-p:Platform=ARM64 -p:RuntimeIdentifier=win-arm64`. More setup and release notes are in [development](docs/development.md) and [the GitHub release process](docs/release.md).

## Privacy and network use

There are no accounts, streaming, telemetry or cloud sync. The weekly update check can be disabled. LRCLIB is contacted only after you request a search, and only the track title and artist are sent. No audio files or file paths are sent to either service.

Crash and error logs are stored locally at `%LOCALAPPDATA%\BrackenVale\Logs`. Open the folder or export a ZIP from Settings. Logs may contain local file paths and error details; review them before sharing.

## License

Bracken Vale source is MIT licensed. Third-party components and their notices are listed in [ThirdPartyNotices.md](ThirdPartyNotices.md).
