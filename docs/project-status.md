# Project status

Updated: 2026-10-09. This tracker separates code that exists from checks that still need a real Windows run. A source checkbox does not mean the feature has passed runtime validation.

## Implemented in the current source

- [x] The desktop app uses the WebView2 interface for library browsing, search, playlists, queue, lyrics, settings, and the immersive player. The native shell handles playback, Windows media controls, file activation, folder pickers, and the separate video window.
- [x] Library scanning, file watching, stale-path reconciliation, offline-root preservation, artwork repair, database migration/recovery, and scan cancellation recovery are implemented in the current source.
- [x] Lyrics editing, embedded and sidecar lyrics, LRCLIB search, queue mutations, playback history, themes, transparency, keyboard controls, and taskbar playback controls are wired through the app bridge.
- [x] The release workflow defines x64 and ARM64 portable packages, per-user setup installers, and MSIX packages. The setup and portable package scripts include uninstall/data-cleanup support. This is packaging implementation, not proof that installation and upgrades work on Windows.
- [x] The repository has the core test project, CI workflow, issue and contribution guidance, license, and third-party notices.
- [x] The two failures in the latest recorded PR CI run have corresponding source fixes: the migration test expects schema version 8, and the indexer flushes completed tracks when a scan is cancelled.
- [x] The latest queue reorder implementation uses pointer capture like Settings and preserves the queue grip icon. Full-screen lyrics styling removes the visible panel fill and border while keeping a scrollable column.

## Still required before a stable release

- [ ] **Prepare one candidate snapshot.** The current worktree has uncommitted queue and lyrics WebView changes. Review and commit the intended changes, then build from that exact revision.
- [ ] **Rerun required CI on the candidate.** PR #2 is still open at commit `1f0042c`; its latest CI run failed all three required jobs. The test failures were the old schema assertion (expected 5, actual 8) and the cancellation test (expected 16 saved tracks, actual 0). The fixes are present in the newer local source, but have not passed CI on the candidate revision.
- [ ] **Verify the installed app on Windows.** On a clean install/data state, add a music folder, scan it, confirm songs and artwork appear, restart the app, and confirm the library remains. Then smoke-check search, playback and queue transitions, queue dragging, lyrics selection, settings persistence, the video window, and taskbar controls. The previous zero-song report has not been closed by a successful installed-build check.
- [ ] **Verify distribution packages.** Install, launch, upgrade, and uninstall the setup package; check its data-retention choice. Install and launch MSIX and verify its declared file actions. The workflow and scripts exist, but these package behaviors have not been confirmed on Windows.
- [ ] **Pass signing preflight.** The latest recorded preflight, run `37403559751` on 2026-10-06, failed and predates the current package identity. Configure the current trusted Code Signing certificate and secrets, then run the private preflight on `main`.
- [ ] **Validate supported media on Windows.** The format matrix still marks every listed audio format as pending playback validation. Check representative files and output devices, and update [format-matrix.md](format-matrix.md) with observed results.
- [ ] **Merge and publish only after the checks above pass.** PR #2 is open; there are no release tags or GitHub Releases yet.

The Windows CI workflow runs the core test project and builds and startup-smokes the x64 and ARM64 portable apps. It does not automate the WebView interaction checks listed above.
