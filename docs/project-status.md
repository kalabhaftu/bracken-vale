# Project status and completion tracker

Updated: 2026-10-04  
Reviewed revision: implementation PR #1, commit `baf48c4` (verification-documentation follow-up is in progress)

This tracker keeps source implementation separate from verified behavior. A checked item under “Implemented” means the behavior is present in the code. A checked item under “Verified” records an executed check and its result.

## Implemented

- [x] SQLite v1 to v2 migration makes a database backup, adds trigram FTS5 indexes and scan-generation tables, and preserves tracks, settings, playlists, playback session and tag-backup rows.
- [x] Search retains the previous short-query `LIKE` behavior, uses FTS5 substring candidates for queries of three or more characters, returns stable database pages, and limits each visible library or playlist page to 200 rows. The UI debounces search changes and cancels/discards stale query results.
- [x] Scan generations record seen, excluded and completed paths in SQLite. Cleanup runs only after complete roots; offline, interrupted, inaccessible, system and reparse-point paths are preserved. User-configured ignored folders keep their previous remove-from-index behavior after a complete root scan.
- [x] Playlist selection, duplicate-preserving page display, play, export, rename, delete and removal actions are wired to the selected playlist entry. Root management adds and removes library folders without deleting media or playlist paths.
- [x] Scan UI displays elapsed time, counts and current path, with pause/resume/cancel feedback. First launch still discovers mounted fixed drives.
- [x] Shuffle and A–B repeat expose their active state. The player, navigation and Now Playing layout adapt below 980/900/760 px, keep Queue and Equalizer controls reachable, use accessible names for compact icons, and follow the existing Fluent/Segoe UI style.
- [x] Search pages cap visible track rows, artwork decodes to thumbnail size and uses an LRU cache estimated at 64 MB, and successful scans/root removal delete only artwork files no longer referenced by the index.
- [x] Tag edits and restores copy in cancellable chunks, retain an undo copy on restore, and keep the newest five backup files and history records per exact track path.
- [x] Playback state mutations are serialized, the second LibVLC player is lazy-created for crossfade, stale restore seeks are guarded by media generation, volume writes are debounced, and crossfade settings are cached.
- [x] LRCLIB and GitHub update requests have bounded timeouts. Logs rotate at 4 MB, retain 14 days, and enforce a 50 MB total cap.
- [x] GitHub Actions keeps the check names `Core tests · Linux`, `x64` and `ARM64`; actions are pinned to commit SHAs, permissions are restricted by job, and ARM64 uses the native `windows-11-arm` runner. Release signing preflights the certificate and a disposable signature; invalid/missing signing blocks stable tags while previews can publish unsigned portable ZIPs.
- [x] Focused core tests cover migration, search compatibility and paging, root removal, playlist occurrences, interrupted/excluded scans, backup retention and restore/cancel, and artwork cleanup. A deterministic 100,000-track search benchmark is included for Windows x64.

## Verified

- [x] Core suite on this Windows x64 machine: **36 passed, 0 failed** (`dotnet test tests/BrackenVale.Tests/BrackenVale.Tests.csproj -c Release`).
- [x] Deterministic 100,000-track first-page search: **local Windows x64 p95 14.2 ms, maximum 20.1 ms**, below the 250 ms target; the Windows x64 CI job also passed the same asserted benchmark.
- [x] `git diff --check` completed without whitespace errors.
- [x] Required PR checks passed on commit `baf48c4`: `Core tests · Linux`, `x64`, and `ARM64` ([run 37217758699](https://github.com/kalabhaftu/bracken-vale/actions/runs/37217758699)).
- [x] Windows x64 and native ARM64 source builds and portable publishes succeeded; both passed the workflow's eight-second startup smoke test.
- [ ] Local `dotnet build`/`dotnet run`. **Not verified on this workstation:** NuGet package downloads timed out and then failed DNS resolution, including through the public mirror. The Windows CI build passed, but this is still a local dependency-restore failure.
- [ ] Manual UI use: library scan/playback, restart persistence, playlists, tag edit/restore, lyrics, tray/media controls, audio devices, keyboard/screen-reader operation, and narrow/high-DPI layout.
- [ ] Supported format/device matrix. `docs/format-matrix.md` remains pending until actual Windows playback tests are completed.
- [x] `main` branch protection requires a pull request plus `Core tests · Linux`, `x64`, and `ARM64` with up-to-date branch checks. No reviewer approval is required; administrators are included.
- [ ] Signing preflight against the configured repository secrets. Secret presence does not establish certificate trust, publisher match, validity or working private-key access. No release has been published by this work.
- [ ] Signed installer/MSIX installation and upgrade/uninstall. Stable release stays blocked until the preflight succeeds.

## Known limits

- NuGet connectivity prevented a local WinUI build and launch during this pass. Retry restore when `api.nuget.org` is reachable, then run the manual Windows checks above.
- A new trusted signature may still receive SmartScreen warnings while publisher reputation develops. Third-party antivirus results are outside the release workflow's control.
- No format or audio-device combination is marked verified without a real Windows playback check.
