# Project status

Updated: 2026-10-09. This tracker separates code that exists from checks that still need a real Windows run. A source checkbox does not mean the feature has passed runtime validation.

Current optimization commits and measured results are recorded in
[optimization-validation.md](optimization-validation.md): 73 core tests and 10
native tests passed locally; x64/ARM64 self-contained publishes succeeded;
component selection reduced the preliminary x64 publish by 15.7%; schema 9
checkpoints wrote 4,152 WAL bytes with both small and 100,000-entry queues.
Signing now explicitly uses a persistent self-signed certificate. Expected GitHub
secrets are configured. Private candidate packaging and installed interaction
validation remain release gates. The running user instance was left undisturbed.

## Implemented in the current source

- [x] The desktop app uses the WebView2 interface for library browsing, search, playlists, queue, lyrics, settings, and the immersive player. The native shell handles playback, Windows media controls, file activation, folder pickers, and the separate video window.
- [x] Library scanning, file watching, stale-path reconciliation, offline-root preservation, artwork repair, database migration/recovery, and scan cancellation recovery are implemented in the current source.
- [x] Lyrics editing, embedded and sidecar lyrics, LRCLIB search, queue mutations, playback history, themes, transparency, keyboard controls, and taskbar playback controls are wired through the app bridge.
- [x] The release workflow defines x64 and ARM64 portable packages, per-user setup installers, and MSIX packages. The setup and portable package scripts include uninstall/data-cleanup support. This is packaging implementation, not proof that installation and upgrades work on Windows.
- [x] The repository has the core test project, CI workflow, issue and contribution guidance, license, and third-party notices.
- [x] The two failures in the earlier PR CI run have corresponding source fixes: the migration test now expects schema version 9, and the indexer flushes completed tracks when a scan is cancelled.
- [x] The latest queue reorder implementation uses pointer capture like Settings and preserves the queue grip icon. Full-screen lyrics styling removes the visible panel fill and border while keeping a scrollable column.

## Still required before a stable release

- [x] **Prepare candidate source snapshots.** Playback/UI work is preserved and optimization changes are committed in focused groups. Final validation must still identify the final tested revision.
- [ ] **Rerun required CI on the candidate.** PR #2 is still open at commit `1f0042c`; its latest CI run failed all three required jobs. The test failures were the old schema assertion (expected 5, actual 8) and the cancellation test (expected 16 saved tracks, actual 0). The fixes are present in the newer local source, but have not passed CI on the candidate revision.
- [ ] **Verify the installed app on Windows.** On a clean install/data state, add a music folder, scan it, confirm songs and artwork appear, restart the app, and confirm the library remains. Then smoke-check search, playback and queue transitions, queue dragging, lyrics selection, settings persistence, the video window, and taskbar controls. The previous zero-song report has not been closed by a successful installed-build check.
- [ ] **Verify distribution packages.** Install, launch, upgrade, and uninstall the setup package; check its data-retention choice. Install and launch MSIX and verify its declared file actions. The workflow and scripts exist, but these package behaviors have not been confirmed on Windows.
- [ ] **Pass signing preflight.** The earlier run `37403559751` failed and predates the current package identity. The persistent self-signed certificate and expected secrets are configured; private candidate signing and the final preflight on `main` must pass. This is not publicly trusted signing.
- [ ] **Validate supported media on Windows.** The format matrix still marks every listed audio format as pending playback validation. Check representative files and output devices, and update [format-matrix.md](format-matrix.md) with observed results.
- [ ] **Merge and publish only after the checks above pass.** PR #2 is open; there are no release tags or GitHub Releases yet.

The Windows CI workflow runs the core test project and builds and startup-smokes the x64 and ARM64 portable apps. It does not automate the WebView interaction checks listed above.
