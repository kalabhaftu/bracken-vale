# Project status

Updated: 2026-10-10. Implementation and observed validation are recorded separately.
No stable tag or public GitHub Release has been created. The running user instance
has remained undisturbed; GUI and package tests run on disposable GitHub runners.

## Reviewed implementation

Playback ownership/generation guards fix native end/error callbacks and seeking
after a song ends. Queue revisions avoid repeated full-library reads and queue
hash allocations. Schema 9 stores ordered queue entries separately, preserving
duplicate/unavailable paths. Five-second state checkpoints no longer rewrite
queues; recovery, migration backups, durability and tag backups remain intact.
Startup loads saved library data before background work. Watchers batch affected
directories and bound overflow scans. Small discovery batches become visible
before full-drive scanning finishes. WebView minimizes using official suspension
APIs while native playback advances. WinUI component selection removes unused
SDK payloads. The launcher explicitly builds x64 and preserves a running instance.
Completed shared changes in `2495a47` isolate music crossfade volumes and wait
for incoming decoder readiness; `2eb5212` allows untimed local lyrics to obtain
timed display lyrics while preserving the local file and offline fallback.
These changes require their own final-source CI and package validation.

Signing uses one persistent **self-signed** project key, with expected Actions
secrets configured. Portable/setup/MSIX pipelines sign first-party binaries and
helpers and retain vendor signatures. Installed-package and performance gates
are automated. Final source must have a GitHub-verified commit signature; upload
verification precedes draft-release publication. See [release.md](release.md).

## Observed results and open gates

- Core suite: 88/88 passed on Linux/x64/ARM64 at `eeb4711`, with no failures
  or skips. The required Linux check also passed all 11 Node regression cases.
  CI [38047712311](https://github.com/kalabhaftu/music-player/actions/runs/38047712311)
  passed all three protected checks. Commands and behavior coverage are recorded
  in [lyrics-and-artwork-fixes.md](lyrics-and-artwork-fixes.md).
- Native playback/format suite: 49/49 passed on x64 and ARM64 at `eeb4711`,
  CI [38047712311](https://github.com/kalabhaftu/music-player/actions/runs/38047712311).
  Genuine fixtures cover all 19 audio extensions, tag editing or safe read-only
  rejection, video rendering, subtitles, rate changes, seeking and snapshots.
  Ogg seeking, WAV alias metadata and DFF opening/duration failures are fixed.
- Both x64 and ARM64 portable UI at `eeb4711` completed discovery, search, duplicate queue
  dragging, immersive lyrics, 60 navigations, minimized queue advancement,
  tray restore, taskbar/media keys and restart persistence. Video controls,
  subtitle selection, real fullscreen/Escape, PNG snapshots, and artwork-accent
  contrast in light/dark themes also passed. Initial and restart checks both ran.
  CI [38047712311](https://github.com/kalabhaftu/music-player/actions/runs/38047712311).
  The earlier x64 run at `a73e62f`,
  CI [38003017907](https://github.com/kalabhaftu/music-player/actions/runs/38003017907)
  recorded 1,208,320 bytes of private-memory growth in the final 20 navigations,
  below the 32 MiB regression limit.
  ARM64's disposable Windows desktop required initialization of its first-login
  privacy screen and dismissal of a Microsoft-account welcome window owned by
  Windows `WWAHost`. Desktop-app enumeration missed that packaged welcome UI;
  the test now uses the actual obstructing HWND, exact title, system-process
  ownership and session checks. Real input assertions remain required and passed.
  `368dabb` also fixes a real cancellation bug exposed during these tests:
  pending watcher recovery immediately restarted an explicitly cancelled scan.
  Automatic watcher work now waits five minutes, preserving pending changes and
  allowing immediate manual scans. Installed package and final-source gates
  still require their own completed evidence.
- Private signed candidate `eeb4711`, run
  [38048035180](https://github.com/kalabhaftu/music-player/actions/runs/38048035180),
  built and authenticated all required assets. Complete x64 and ARM64 MSIX
  gates passed, including UI, upgrade, associations and both uninstall choices.
  Setup's initial UI checks passed, but upgrade exited with code 5 on both
  architectures. The artwork-fixture helper loaded the installed TagLib DLL
  into the long-lived test host. `a0fb3fb` moves that work into a hidden child
  process that exits before upgrade and retains installer logs. Setup and
  final-source package gates require a successful rerun.
  Windows PowerShell hosts Appx tests; the shipped PowerShell 7 helper uses the
  official Windows PowerShell compatibility import.
- The complete x64 performance gate passed at `9c2f75c`, private run
  [38000118310](https://github.com/kalabhaftu/music-player/actions/runs/38000118310).
  Unpacked packages were 14.0% smaller; signed portable ZIPs were 13.2% smaller.
  Visible-library times fell from 15,502 to 4,744 ms for 100 saved tracks and
  12,740 to 7,961 ms for 100,000 tracks. The large baseline's resource sample
  still had an active scan, so its CPU measurement is explicitly not idle.
  The complete performance gate at `eeb4711` also passed both x64 and ARM64
  unpacked and downloadable ZIP size targets. Final main validation remains
  required. Constant-size database
  checkpoint and combined-memory results are in [optimization-validation.md](optimization-validation.md).
- Follow-up shared changes: 15/15 Node lyric checks passed locally; seven
  crossfade checks passed using cached VLC 3.0.23.1. The tracked release engine
  remains 3.0.24 and is validated on CI runners. Three real Windows audio-device
  cases require an audio endpoint; their cached-engine evidence is separate
  from headless CI. See [music-crossfade.md](music-crossfade.md).
- `9c2f75c` adds installed video controls/subtitle/screenshot checks and repairs
  video file activation while retaining the opt-in video-extension setting.
  Final release commits must be GitHub-created (`web-flow`) and verified; this
  provides the requested GitHub.com signature badge, separately from self-signed
  Windows package signatures.

## Still required

- [ ] Pass all protected CI checks on the final candidate, including all formats.
- [ ] Pass complete portable and installed setup/MSIX UI flows on x64 and ARM64.
- [ ] Verify setup/MSIX upgrade, associations and both data-retention choices.
- [ ] Meet final package/resource targets and validate stable navigation memory.
- [ ] Review final PR, protected squash merge, verify GitHub commit signature.
- [ ] Synchronize main; pass main signing preflight and final package validation.
- [ ] Delete local backup branch only after the verified external bundle is retained.
- [ ] Create `v1.0.0`, publish authenticated signed assets and verify downloads.

PR #2 remains open on `codex/update-verification-status`. Do not interpret a
passing build, partial UI run or self-signature as completion of these gates.
