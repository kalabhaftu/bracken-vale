# Optimization and release validation

Updated 2026-10-10. Candidate work is on `codex/update-verification-status`.
Playback changes in `9759726` and UI changes in `393ee8c` are preserved.
Optimization commits: `73981a8` (schema 9), `0c9c61c` (startup, watchers,
queue revisions and presentation), `00ccd77` (components/launcher),
`e24d59d` (self-signing/candidate packaging). No stable tag or release has been
created. Passing builds are not installation or interaction evidence.

## Verified locally

- Core suite: 76/76 passed with `dotnet test tests/MusicPlayer.Tests/MusicPlayer.Tests.csproj --configuration Release --no-restore`. Includes schema-9 migration backups, duplicate/unavailable queue paths, queue recovery, unchanged writes, 100,000-entry checkpoints, watcher batching/overflow throttling, library/queue context consistency, WAV alias metadata and DFF duration/indexing.
- Final native engine evidence at `19577d5` and `cf0c9a9`: 49/49 passed on both x64 and ARM64 with the fixture environment prepared as documented in development.md. Silent WAV/dummy output covers natural advancement, final-queue stop, seeking after end, paused/restored seek, repeat, successful and failed crossfade, native open errors, and queue revision behavior. Genuine fixtures additionally cover all 19 extensions, metadata preservation, video and subtitles. Local cached-engine checks are supplementary.
- Every `WebUI/scripts/*.js` passed `node --check`. Packaging PowerShell files passed the PowerShell parser. These are syntax checks, not GUI checks.
- x64 build succeeded with zero warnings/errors using an isolated `BaseOutputPath`; x64 and ARM64 self-contained publishes succeeded. The running user instance was not stopped or replaced.
- Preliminary x64 unpacked publish comparison: 321.94 MiB / 960 files before component selection, 271.36 MiB / 902 files after: 15.7% smaller. LibVLC modules/notices retained. Final signed archive/setup/MSIX measurements are pending.
- Managed trimming was evaluated only in `artifacts/trimming-evaluation`. Unresolved IL2026/IL2104 diagnostics in JSON/WinRT/TagLib prevent adoption. Production publishes remain untrimmed.
- Persistent self-signed RSA/SHA-256 `CN=Kalabhaftu` certificate generated; public certificate and fingerprint committed. Expected `MUSICPLAYER_SIGNING_PFX_BASE64` and `MUSICPLAYER_SIGNING_PFX_PASSWORD` secrets configured via stdin. Private material is protected outside this repository. No end-user trust was installed.
- Backup branch has 14 unique historical commits and predates substantial current features. Full-history bundle verified at `%LOCALAPPDATA%/MusicPlayerGitArchives/backup-pre-squash-music-player-v2-20261006.bundle`; SHA-256 `0569FA677F8F968E9239F48055F4738C38D70EA5C22D2CFCC239954EE3624D10`. Branch retained until release gates complete.

## Database benchmark

Command: `dotnet run --project tests/MusicPlayer.Benchmarks/MusicPlayer.Benchmarks.csproj --configuration Release -- artifacts/performance-20261009`.
The old checkpoint algorithm is reproduced exactly: full-session JSON serialization and upsert. Measurements are single local samples, not end-to-end GUI startup results.

| Tracks / queue entries | Old checkpoint WAL bytes | State-only WAL bytes | Old checkpoint ms | State-only ms | First 100 library rows ms |
|---:|---:|---:|---:|---:|---:|
| 100 | 16,512 | 4,152 | 2.19 | 1.93 | 25.99 |
| 100,000 | 11,573,112 | 4,152 | 636.94 | 2.40 | 56.78 |

FULL durability, WAL, playlists, tag backups and recovery copies remain enabled.
No routine startup VACUUM was introduced.

## Observed Windows measurements (not final acceptance)

Private run [37996130358](https://github.com/kalabhaftu/music-player/actions/runs/37996130358)
compared baseline `9759726` with `19577d5` on the same disposable x64 runner.
For 100 actual saved tracks, time to visible library rows fell from 11,242 ms to
4,596 ms. Combined native/WebView private memory after ten idle seconds was
205,488,128 bytes before and 202,461,184 bytes after; working set was 493,707,264
and 484,855,808 bytes. Ten-second idle CPU consumption fell from 0.78125 to
0.375 CPU-seconds. These are single measurements, not a universal speed claim.
The 100,000-track baseline scan did not cancel within 60 seconds, preventing
that run from reaching final package acceptance; this limitation is now recorded
explicitly and the full comparison is rerunning.

The complete x64 UI run at `19577d5` recorded combined private memory of
239,603,712, 243,122,176 and 243,879,936 bytes after successive warmed navigation
batches. The final 20 navigations grew by 757,760 bytes. Current UI checks enforce
a 32 MiB limit over the final warmed batch and retain the raw samples.

## Remaining stable-release gates

- [ ] Required `Core tests · Linux`, `x64`, and `ARM64` checks on the final candidate.
- [ ] Private signed candidate packaging, timestamps, first-party assemblies/scripts, setup and embedded-uninstaller signatures, MSIX signatures, authenticated manifest verification.
- [ ] Clean Windows installed-library discovery and restart persistence; search, queue dragging/selection, lyrics, settings, tray/taskbar/media keys, video/subtitles.
- [ ] Setup and MSIX install, upgrade, uninstall, file associations and both data-retention choices on isolated x64/ARM64 environments.
- [ ] Actual representative playback results for every format in `format-matrix.md`.
- [ ] Before/after GUI time to usable library, CPU and combined native/WebView2 memory for small and 100,000-track fixtures; repeated-navigation and minimized-playback checks.
- [ ] Protected PR merge; synchronize main; signing preflight and final package validation from merged revision.
- [ ] Delete the archived local backup branch only after gates pass; tag `v1.0.0`, publish self-signed assets, and verify uploaded hashes.

Signing is explicitly self-signed, **not publicly trusted**. See
[`SIGNING.md`](../packaging/windows/SIGNING.md). Publisher/SmartScreen warnings and
the MSIX certificate-trust choice must remain explicit in release notes.
