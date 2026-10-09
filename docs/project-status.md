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

- Core suite: 76/76 passed locally and on Linux/x64/ARM64 at `19577d5`.
- Native playback/format suite: 49/49 passed on x64 and ARM64 at `19577d5`,
  CI [37996036525](https://github.com/kalabhaftu/music-player/actions/runs/37996036525).
  Genuine fixtures cover all 19 audio extensions, tag editing or safe read-only
  rejection, video rendering, subtitles, rate changes, seeking and snapshots.
  Ogg seeking, WAV alias metadata and DFF opening/duration failures are fixed.
- x64 portable UI completed discovery, search, duplicate queue dragging, immersive
  lyrics, 60 navigations, minimized queue advancement, tray restore, taskbar/media
  keys and restart persistence. ARM64 tray registration still fails with Windows
  E_FAIL and a correctly sized 976-byte NOTIFYICONDATA. An independent Windows
  notification probe is being added; this gate remains open.
- Private signed candidate [37996130358](https://github.com/kalabhaftu/music-player/actions/runs/37996130358)
  authenticated all required assets. x64 setup installation and upgrade completed
  four UI checks; explicit data-removal uninstall failed because Inno Setup checks
  are evaluated at installation. `efc6241` moves the choice into uninstallation.
  MSIX installation has not yet run past the setup gate.
- Performance baseline completed both 100-track runs, then its 100,000-track
  scan failed to cancel within 60 seconds. The next run records that baseline
  limitation without labeling its CPU sample idle. Candidate scan cancellation
  must still settle before its idle measurement. Final package/resource comparison
  remains pending. Constant-size database checkpoint results are recorded in
  [optimization-validation.md](optimization-validation.md).
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
