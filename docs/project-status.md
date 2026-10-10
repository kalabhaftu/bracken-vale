# Project status

Updated: 2026-10-10. Results below refer to immutable tested revisions; later
merge and publication results are available in the linked workflows. The user's
running instance remains untouched. Windows UI/package tests use disposable
GitHub runners. No release binaries are downloaded to the user's PC.

## Reviewed implementation

Playback ownership/generation guards fix end/error callbacks and seeking after
the queue ends. Music crossfades use independent DirectSound buffers and wait for
incoming decoder readiness; failure, pause and seek restore the outgoing track.
Queue revisions avoid whole-library reads and repeated queue signatures.

Schema 9 stores ordered queue entries separately from playback state, preserving
duplicate and unavailable paths. Position checkpoints no longer rewrite queues.
Migration backups, corruption recovery, FULL durability, playlists and tag
backups remain intact. Unchanged settings and paused checkpoints skip writes;
passive WAL maintenance does not introduce startup VACUUM.

Saved rows render before background scans and update checks. Watchers batch
affected directories, bound pending work and throttle overflow recovery and work
after explicit cancellation for five minutes; manual scans remain immediate.
Official WebView suspension reduces minimized rendering work while native
playback continues. Component selection removes unused Windows App SDK payloads
while retaining self-contained runtimes and VLC modules/notices. The launcher
builds the exact x64 output, retries restore once and explains audit-feed warnings
without disabling auditing or restarting a running player.

Completed shared edits improve artwork contrast, timed lyric lookup/offline
fallback, crossfade/output routing and pagination. Grouped collection contents
stay complete; global duplicate hiding retains its Songs context. Paging retains
queue entry identities, retries failed loads and rejects stale responses.

Signing uses one persistent **self-signed** `CN=Kalabhaftu` key. First-party
binaries, PowerShell helpers, setup/embedded uninstaller and MSIX are signed;
vendor signatures remain intact. Authenticode signatures are timestamped. CMS
authenticates the checksum manifest. This is not publicly trusted signing:
publisher/SmartScreen warnings and intentional MSIX certificate trust are
documented in [SIGNING.md](../packaging/windows/SIGNING.md).

## Verified candidate

Tested code: `b1316ef0fa7d8063e32d4f81806cd23ffd2d5bc5`.

- All protected checks passed in [CI 38053176126](https://github.com/kalabhaftu/music-player/actions/runs/38053176126): **Core tests · Linux**, **x64**, **ARM64**.
  There are 101 core cases and 44 Node cases. An independent local rerun passed
  101/101 core and 44/44 Node cases with no failures or skips.
- x64 and ARM64 native tests each passed **53 cases**, with **3 physical audio-device cases
  skipped because the runner has no output device** (56 total). Genuine fixtures
  exercise all 19 audio extensions, metadata/byte-for-byte recovery, read-only
  rejection, video/subtitles, seeking, repeat, crossfade and restart after end.
  The release engine is LibVLC 3.0.24. Local physical-device checks with cached
  3.0.23.1 are supplementary; see [music-crossfade.md](music-crossfade.md).
- Both portable UI gates passed initial and restart checks: automatic discovery
  and persistence, search, duplicate queue dragging, immersive lyrics, settings,
  light/dark artwork contrast, 60 navigations, minimized advancement,
  tray/taskbar/media keys, video/subtitles, real fullscreen/Escape and PNG
  snapshots. Final warmed navigation growth must remain below 32 MiB across
  20 navigations; JavaScript errors fail the gate.
- All private candidate gates passed in [38053565166](https://github.com/kalabhaftu/music-player/actions/runs/38053565166):
  signed portable ZIPs/setup/MSIX, timestamps and authenticated checksums;
  complete installed UI/restart checks on x64 and ARM64; setup/MSIX upgrades
  from 0.9.9, associations, both uninstall data choices, source music and
  user-owned installation-file preservation; and comparative resource gates.
  Publication was intentionally skipped because this was a private candidate.

Commands:

```powershell
dotnet test tests/MusicPlayer.Tests/MusicPlayer.Tests.csproj --configuration Release --no-restore
node --test tests/MusicPlayer.Ui.Tests/lyrics-theme.test.mjs tests/MusicPlayer.Ui.Tests/library-pagination.test.mjs
# CI prepares genuine media fixtures before the native suite.
dotnet test tests/MusicPlayer.Playback.Tests/MusicPlayer.Playback.Tests.csproj --configuration Release
gh workflow run release.yml --ref codex/update-verification-status -f mode=candidate
```

Earlier complete x64 setup/MSIX checks passed at `c1c71d8` in [38050852173](https://github.com/kalabhaftu/music-player/actions/runs/38050852173).
ARM64 lifecycle and MSIX checks passed there; initial setup UI focus failed.
`7827866` uses official foreground activation and waits for the target window
to process it before requiring real input. The previous setup file lock came
from the fixture host loading installed TagLib; `a0fb3fb` isolates this helper
in a child process that exits before upgrade. Independent lifecycle checks
continue after interaction failures, but the failed interaction still fails the job.

## Resource and history evidence

The completed `eeb4711` comparison, [38048035180](https://github.com/kalabhaftu/music-player/actions/runs/38048035180),
reduced unpacked/ZIP size by **14.0%/13.2% on x64** and **13.4%/12.3% on ARM64**.
The 100-track library became visible in 4,556 ms versus 14,272 ms. Large-library
times were effectively equal, 6,971 versus 6,965 ms; combined private memory fell
from 473,833,472 to 305,250,304 bytes. Large baseline CPU was still scanning and is
not called idle. Position checkpoints write **4,152 WAL bytes** with either 100
or 100,000 queue entries, versus 11,573,112 bytes for the old large checkpoint.
Exact measurements/limitations: [optimization-validation.md](optimization-validation.md).

The final `b1316ef` comparison also passed both architecture size targets:
x64 unpacked/ZIP **14.01%/13.15%** smaller, ARM64 **13.39%/12.25%** smaller.
Small-library visibility was 4,846 versus 11,067 ms; large-library visibility
was 7,311 versus 6,651 ms in this single sample, so startup is not claimed to
improve uniformly. Large combined private memory fell from 556,240,896 to
300,675,072 bytes. Both candidate scans settled; the large baseline remained
scanning during its resource sample. Raw JSON is printed in the job log.

The obsolete remote branches `archive/music-player-v2-checkpoint-20261006` and
`codex/complete-bracken-vale`, and the clean unused `marbled-wolf` worktree,
were removed at the user's request. Their history and pre-squash main/current
branches remain in a verified complete bundle outside the checkout:
`%LOCALAPPDATA%/MusicPlayerGitArchives/music-player-pre-release-squash-20261010-b1316ef.bundle`.
SHA-256: `CCF87C68D873EC8EAFFC876FB2234CF21E7EDF16B9EF0E04B3251E18D41DB7AB`.
The separate backup bundle is also verified; its fingerprint is in
[optimization-validation.md](optimization-validation.md). The local backup branch
was removed after the complete candidate gates passed.

## Remaining steps at this checkpoint

- [x] Final code's required CI, core/native formats and complete portable UI.
- [x] Complete signed setup/MSIX UI, upgrades, associations and both uninstall choices on x64/ARM64.
- [x] Complete final before/after resource and package-size gates.
- [ ] Protected squash merge PR #2; verify GitHub-created `web-flow` source signature.
- [ ] Synchronize main; pass main signing preflight and full merged-revision package validation.
- [x] Remove archived local backup branch after verified candidate gates.
- [ ] Create `v1.0.0`; authenticate uploaded assets on CI before public release.

At this checkpoint PR #2 is open and no stable tag or public release exists.
The [release process](release.md) requires every remaining gate before publication.
