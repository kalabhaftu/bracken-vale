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

Signing uses one persistent **self-signed** project key, with expected Actions
secrets configured. Portable/setup/MSIX pipelines sign first-party binaries and
helpers and retain vendor signatures. Installed-package and performance gates
are automated. Final source must have a GitHub-verified commit signature; upload
verification precedes draft-release publication. See [release.md](release.md).

## Observed results and open gates

- Core suite: 76/76 passed on Linux/x64/ARM64 at `7bfc083`.
- Native playback/format suite: 49/49 passed on x64 and ARM64 at `7bfc083`,
  CI [38036024119](https://github.com/kalabhaftu/music-player/actions/runs/38036024119).
  Genuine fixtures cover all 19 audio extensions, tag editing or safe read-only
  rejection, video rendering, subtitles, rate changes, seeking and snapshots.
  Ogg seeking, WAV alias metadata and DFF opening/duration failures are fixed.
- x64 portable UI at `a73e62f` completed discovery, search, duplicate queue
  dragging, immersive lyrics, 60 navigations, minimized queue advancement,
  tray restore, taskbar/media keys and restart persistence. Video controls,
  subtitle selection, real fullscreen/Escape, and PNG snapshots also passed.
  CI [38003017907](https://github.com/kalabhaftu/music-player/actions/runs/38003017907)
  recorded 1,208,320 bytes of private-memory growth in the final 20 navigations,
  below the 32 MiB regression limit.
  ARM64's independent Windows tray probe failed because the disposable image
  remained on its first-login privacy screen. Initializing that desktop with
  Windows' OOBE policy fixed the stock probe at `8d37bbb`, diagnostics
  [38004081735](https://github.com/kalabhaftu/music-player/actions/runs/38004081735).
  x64 also passed the complete portable UI gate at `7bfc083`; ARM64 failed because
  its Start menu retained foreground focus during the fullscreen Escape check.
  `3971bdd` dismisses only that observed Windows system menu before activation;
  the real keyboard and window-bounds assertions remain required.
- Private signed candidate `fc99848`, run
  [38005548773](https://github.com/kalabhaftu/music-player/actions/runs/38005548773),
  built and authenticated all required assets. Its complete x64 MSIX gate passed,
  including UI, upgrade, associations and both uninstall data choices. Setup
  lifecycle checks completed, but its UI gate failed on a playback-state timing
  race. ARM64 setup/MSIX lifecycle checks ran independently; their UI gate failed
  on foreground activation. These partial results do not close the package gate.
  Windows PowerShell hosts Appx tests; the shipped PowerShell 7 helper uses the
  official Windows PowerShell compatibility import.
- The complete x64 performance gate passed at `9c2f75c`, private run
  [38000118310](https://github.com/kalabhaftu/music-player/actions/runs/38000118310).
  Unpacked packages were 14.0% smaller; signed portable ZIPs were 13.2% smaller.
  Visible-library times fell from 15,502 to 4,744 ms for 100 saved tracks and
  12,740 to 7,961 ms for 100,000 tracks. The large baseline's resource sample
  still had an active scan, so its CPU measurement is explicitly not idle.
  The complete performance gate at `fc99848` also passed both x64 and ARM64
  unpacked and downloadable ZIP size targets. Final main validation remains
  required. Constant-size database
  checkpoint and combined-memory results are in [optimization-validation.md](optimization-validation.md).
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
