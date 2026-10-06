# Music Player

Music Player is a Windows desktop app for playing and organizing a local music library. It uses a WinUI 3 window with a packaged WebView2 interface. C# remains responsible for playback, library access, playlists, settings, filesystem operations, and Windows integrations.

## Features

- Search and browse songs, albums, artists, genres, favorites, history, and playlists.
- Scan local folders with exclusions, progress, pause, resume, and cancel controls.
- Manage the queue, shuffle, repeat, A–B repeat, volume, crossfade, output device, and ten-band equalizer.
- Edit supported tags with backups; view lyrics, search LRCLIB on request, and inspect track details.
- Find exact duplicate files, inspect their locations, and open them in File Explorer.
- Use Windows media controls, media keys, file activation, optional tray behavior, and paused-session restore.

## Build

Use Windows 10 version 1809 or later, the .NET 10 SDK, and Windows SDK 10.0.26100 or later.

```powershell
dotnet restore MusicPlayer.sln
dotnet build src/MusicPlayer.App/MusicPlayer.App.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64
dotnet run --project src/MusicPlayer.App/MusicPlayer.App.csproj
```

For ARM64, use `-p:Platform=ARM64 -p:RuntimeIdentifier=win-arm64`. The core tests can be run with `dotnet test tests/MusicPlayer.Tests/MusicPlayer.Tests.csproj -c Release`.

## Local files and network access

The SQLite database, settings, artwork cache, tag backups, and logs remain under `%LOCALAPPDATA%\BrackenVale` to preserve existing user data. The app has no account, streaming service, or cloud sync. LRCLIB receives only title and artist after a user starts a lyrics search. The optional weekly update check contacts GitHub and can be disabled in Settings.

The temporary `local_music_player.html` and `local_music_player_v2.html` files were local design inputs only. They are now kept outside the checkout and are not needed for builds or included in releases.

Windows format support depends on the bundled LibVLC modules and the specific file/device combination. See [the format matrix](docs/format-matrix.md). Release signing requirements and current signing status are documented in [the release process](docs/release.md). Remaining migration and release checks are listed in [project status](docs/project-status.md).

## License

The source is MIT licensed. Third-party components and notices are listed in [ThirdPartyNotices.md](ThirdPartyNotices.md).
