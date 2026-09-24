# Product

<!-- impeccable:product-schema 1 -->

## Platform

windows

## Users

People who keep a personal music collection on Windows and want to play, organize, and edit it locally.

## Product Purpose

Bracken Vale is an offline-first desktop music library and player. Success means a user can find and play music across local folders, keep playlists and listening state on their own device, and manage their music without an account or a streaming service.

## Positioning

It combines a searchable, path-aware local library with native Windows playback and music management; playlists can refer to tracks in different folders without moving the files.

## Operating Context

The app indexes internal fixed drives on first launch, with removable and network drives opt-in. People can choose ignored folders, add library folders, pause or cancel scanning, and keep using the library while an incremental scan runs.

## Capabilities and Constraints

- Windows 10 version 1809 and later, including Windows 11; native WinUI 3, C#, .NET, Segoe UI, and Windows Fluent controls. Windows-only runtime; CLI development and Windows CI, no Visual Studio IDE requirement.
- Local playback through bundled LGPL LibVLC modules; TagLib# for metadata. Publish only a verified format matrix and required notices. Codec patent licensing and exact supported formats remain release checks.
- Local SQLite library, playlists, settings, favorites, five-star ratings, play counts, lyrics, and paused session. Restore sessions paused. M3U8 UTF-8 is the playlist interchange format.
- Metadata changes require preview, explicit save, and a recoverable backup. Embedded and sidecar LRC lyrics are supported; LRCLIB is contacted only after a user requests a search, using title and artist only.
- Themes include System, Light, Dark, and a user-switchable artwork-accented mode. Users can manually override the accent. No accounts, telemetry, streaming, or cloud sync.
- GitHub Releases can be checked weekly, disabled in settings, and linked from an in-app notice; updates are never downloaded or installed automatically.
- Release targets are x64 and ARM64 with self-contained portable ZIP, setup EXE, and signed MSIX bundle. CLI development uses .NET and Windows SDK prerequisites, not Visual Studio IDE.

## Brand Commitments

The product is named Bracken Vale. Its UI should feel like a well-finished Windows 11 application: native Fluent controls and consistent Windows icons, with a second album-art-led accent style users can switch to.

## Evidence on Hand

No existing product assets or user media are present in this checkout. Never present fabricated album art or library entries as real user data.

## Product Principles

- Local files and user control come first.
- The library is useful while scanning and remains responsive.
- Changes to music files are explicit and recoverable.
- Native Windows interaction conventions should remain familiar.
- Online features require a direct user action except for the optional weekly release check.
