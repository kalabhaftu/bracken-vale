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

- Core suite: 74/74 passed on Linux, x64 and ARM64 at `0554caa`, CI
  [37980162002](https://github.com/kalabhaftu/music-player/actions/runs/37980162002).
  A new `.wave` metadata/index/edit/recovery regression also passed locally;
  final complete-suite validation is pending the next candidate revision.
- Native suite at `0554caa`: x64 46/49, ARM64 45/49. Video rendering, embedded
  subtitles, rate changes, seeking and PNG snapshots passed on both architectures.
  Failures: default Ogg/OGA seeking, `.wave` metadata alias, and ARM64 DFF opening.
  Local raw-engine reproduction confirms VLC's bundled FFmpeg Ogg reader fixes
  seeking. The equivalent DFF experiment failed and was removed. DFF remains open.
- Windows UI at `0554caa`: clean-profile discovery, search, queue dragging with
  duplicate entries, immersive lyrics and repeated navigation passed up to later
  native-control gates. x64 tray restoration/minimized queue advancement/taskbar
  controls reached the media-key gate, which failed. ARM64 lacked a working tray
  notification area. Complete restart/installed interaction checks remain open.
- Private package run
  [37980181914](https://github.com/kalabhaftu/music-player/actions/runs/37980181914)
  built signed portable/setup/MSIX packages, then failed manifest verification
  because the required-assets list omitted the MSIX uninstall helper. The list is
  corrected in the next candidate. Installed and performance jobs did not run.
- Preliminary package reduction and constant-size checkpoint measurements are in
  [optimization-validation.md](optimization-validation.md). Final signed package
  size, usable-library startup, CPU and native-plus-WebView memory measurements
  for 100/100,000 tracks remain gated on successful candidate packaging.

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
