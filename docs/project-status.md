# Project status and checklist

Updated: 2026-10-04  
Reviewed revision: `main` at `a5b7d26`

This is an implementation review from the source code. “Implemented” means the behavior is present in code; it does not mean the Windows app has been manually tested. The local `dotnet run` attempt has not produced a runnable app in the checkout.

## Current snapshot

- [x] Core library and WinUI app have substantial working implementations; this is not an empty starter app.
- [x] Core unit tests are present in `tests/BrackenVale.Tests/CoreTests.cs`.
- [ ] Confirm the local app restore/build finishes and the window starts. **Not complete:** the `dotnet` process is no longer running, NuGet's app-project cache records failed package downloads, and no app executable is present under `src/BrackenVale.App/bin`.
- [ ] Check current GitHub Actions results. **Not checked:** GitHub API was unreachable during this review.
- [ ] Choose the next product feature with the user; no user-prioritized feature request is recorded here yet.

## Implemented in source

- [x] **Library and scanning:** SQLite index, metadata extraction, search, sort, album/artist/genre/folder views, favorites, ratings, and scan pause/resume/cancel. See `src/BrackenVale.Core/LibraryStore.cs`, `LibraryScanner.cs`, `LibraryIndexer.cs`, and `src/BrackenVale.App/MainWindow.xaml.cs` (`StartScan`, `RefreshLibrary`).
- [x] **Playlists:** core CRUD and M3U/M3U8 import/export, with order and duplicate entries preserved. See `LibraryStore.cs`, `Playlists.cs`, and `MainWindow.xaml.cs` (`NewPlaylist_Click`, `ImportPlaylist_Click`, `ExportPlaylist_Click`, `DeletePlaylist_Click`). The selected-playlist UI has a confirmed bug listed below.
- [x] **Playback:** LibVLC playback, queue controls, seek, repeat, shuffle, A–B repeat, crossfade, volume, equalizer, and saved playback session. See `src/BrackenVale.App/PlaybackService.cs` and `MainWindow.xaml.cs` (`AdvanceQueue`, `ShowQueueAsync`, `Equalizer_Click`, `SaveSession`, `RestoreSession`).
- [x] **Music management:** tag editing with recoverable backup/restore, lyrics editing and LRC sidecars, favorites, ratings, details, and reveal-in-Explorer actions. See `src/BrackenVale.Core/TagEditor.cs`, `Lyrics.cs`, `LyricsFiles.cs` and `MainWindow.xaml.cs` (`EditTags_Click`, `RestoreTags_Click`, `EditLyricsAsync`).
- [x] **Settings and Windows integrations:** themes/materials, tray behavior, audio-output selection, Windows media controls, optional weekly release checks, and local logs. See `MainWindow.xaml.cs` (`ShowSettingsAsync`, `InitializeSystemMediaControls`, `CheckForUpdatesAsync`), `TrayIconService.cs`, `GitHubUpdates.cs`, and `LocalAppLog.cs`.
- [x] **Build/release automation:** CI source builds and publishes x64/ARM64 portable ZIPs; tagged releases can also create signed setup EXEs and an MSIX bundle when signing secrets are configured. See `.github/workflows/ci.yml` and `.github/workflows/release.yml`. This confirms workflow code exists, not that a recent run or installation passed.

## Confirmed fixes and validation work

- [ ] **Fix playlist selection view.** Selecting a playlist makes `PlaylistView` hidden and `LibraryView` visible (`MainWindow.xaml.cs`, `RefreshLibrary`). The playlist action buttons and `PlaylistTrackList` are inside that hidden view (`MainWindow.xaml`, `PlaylistView`), so export/delete and playlist-row playback are inaccessible there.
- [ ] **Fix playlist removal.** `RemoveFromPlaylist_Click` asks `PlaylistTrackList` for the row index even when called from the generic `TrackList` context menu. The playlist list is hidden when a playlist is selected, so neither removal route currently works as intended (`MainWindow.xaml.cs`, `RemoveFromPlaylist_Click`).
- [ ] **Add library-root management.** The UI can add roots and configure ignored folders, but has no action to remove a previously added root (`MainWindow.xaml.cs`, `StartStartupScan`, `AddFolder_Click`, `ShowSettingsAsync`).
- [ ] **Improve scan progress feedback.** The scan bar is indeterminate; the core also reports file count, directory count, and current path, but the UI does not expose a percentage, elapsed time, or current path (`MainWindow.xaml`, `ScanProgress`; `LibraryScanner.cs`, `ScanProgress`).
- [ ] **Validate first-run and normal use on this PC:** finish restore/build, open the app, add a music folder, scan, play a real track, and restart to confirm library/session persistence.
- [ ] **Validate playback formats and devices on Windows.** Every format in `docs/format-matrix.md` is marked “Pending Windows validation”; include actual audio output and Bluetooth device switching.
- [ ] **Exercise Windows-only integrations:** media keys, tray minimize/restore, crossfade and A–B repeat, scaling/accessibility settings, and Windows 10/11 behavior. CI starts the x64 app for an 8-second smoke check; it does not cover these interactions.
- [ ] **Exercise release packaging and installation:** check x64 and ARM64 artifacts, and test signed setup/MSIX install, launch, upgrade/uninstall when release credentials are available.
- [ ] **Run the existing core test suite and review its CI result.** Tests are present, but they were not run as part of this source audit.

## Smaller polish opportunities

- [ ] Show persistent active states for Shuffle and A–B repeat; the current controls report changes through notices but do not show their active state.
- [ ] Check search responsiveness with a large library; it refreshes on each text change, and no large-library runtime measurement is recorded.
- [ ] Add UI/integration coverage for the app screens and playback wiring; the current automated suite targets `BrackenVale.Core`.

## Suggested order to discuss

1. Finish the current local restore and make sure the app opens.
2. Fix the playlist screen and removal behavior, since those are confirmed broken paths.
3. Use the app with your library and report the incomplete behavior you want changed next.
4. Validate audio/device behavior, then choose any new features and release work.

No feature beyond these verified issues is assumed to be required. Add the next user-approved task here when we agree on it, then update its checkbox as we implement and verify it.
