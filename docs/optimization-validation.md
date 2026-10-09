# Optimization and release validation

Updated 2026-10-09. Candidate work is on `codex/update-verification-status`.
Playback changes in `9759726` and UI changes in `393ee8c` are preserved.
Optimization commits: `73981a8` (schema 9), `0c9c61c` (startup, watchers,
queue revisions and presentation), `00ccd77` (components/launcher),
`e24d59d` (self-signing/candidate packaging). No stable tag or release has been
created. Passing builds are not installation or interaction evidence.

## Verified locally

- Core suite: 73/73 passed with `dotnet test tests/MusicPlayer.Tests/MusicPlayer.Tests.csproj --configuration Release --no-restore`. Includes schema-9 migration backups, duplicate/unavailable queue paths, queue recovery, unchanged writes, 100,000-entry checkpoints, watcher batching/overflow throttling, and library/queue context consistency.
- Native suite: 10/10 passed with `dotnet test tests/MusicPlayer.Playback.Tests/MusicPlayer.Playback.Tests.csproj --configuration Release --no-restore`. Silent WAV/dummy output covers natural advancement, final-queue stop, seeking after end, paused/restored seek, repeat, successful and failed crossfade, native open errors, and queue revision behavior.
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
