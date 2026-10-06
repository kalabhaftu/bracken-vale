# GitHub release process

GitHub Releases is the distribution channel for Music Player. The Microsoft Store is not part of this release process. The package identity, signing publisher, executable, and local data path retain their existing identifiers for install and data continuity.

## Before merging changes to `main`

Open a pull request. `main` is configured to require the `Core tests · Linux`, `x64`, and `ARM64` checks. No reviewer approval is required by the branch rule. The Windows jobs build and smoke-start the x64 and native ARM64 portable apps; the Linux job runs the core suite.

## Private signing preflight

The Actions secrets are named `BRACKENVALE_SIGNING_PFX_BASE64` and `BRACKENVALE_SIGNING_PFX_PASSWORD`. Secret presence is not proof the certificate is valid or trusted.

Use **Actions → Signed Windows release → Run workflow** on `main` to run the private preflight. A manual run checks the signing certificate's trust chain, validity period, Code Signing purpose, private key and exact `CN=Bracken Vale` publisher. It signs and verifies a disposable text file, removes the temporary signing files, and does not build or publish release assets. The log reports only pass/fail and never prints secret values.

The release workflow implements signing for the executable, setup installers, and MSIX bundle after this preflight passes. The latest recorded preflight ([run 37246401383](https://github.com/kalabhaftu/music-player/actions/runs/37246401383)) failed in the combined signing-validation step, so packaging and signing were skipped and it produced no signed release. That run did not report the failing validation stage. The workflow now logs the stage and exception type on later failures while suppressing certificate and password details. Do not describe signing as successful until a later preflight and artifact verification pass.

If preflight fails, unsigned previews may publish portable ZIPs only. Stable tags fail before packaging until the repository has a publicly trusted, usable certificate whose publisher matches the MSIX identity. Do not use a self-signed certificate for ordinary public distribution.

## Tag a release

1. Update `CHANGELOG.md` with a heading matching the exact version.
2. Merge the tested pull request to `main`.
3. Create and push `vMAJOR.MINOR.PATCH` for stable, or `vMAJOR.MINOR.PATCH-preview.N` for a preview.
4. Review the release workflow result and GitHub Release assets.

The workflow creates self-contained portable ZIPs for x64 and ARM64. Each ZIP extracts into one folder named `MusicPlayer-VERSION-ARCHITECTURE-portable` and includes `Portable-README.txt`, `LICENSE`, `ThirdPartyNotices.md`, and `Register-MusicPlayer-FileActions.ps1`. The README explains how to launch the app, register or remove the portable copy under Open with and in supported audio-file context menus, and that the library database, settings, artwork cache, and logs live in `%LOCALAPPDATA%\BrackenVale`. The setup installer registers file actions for the installing Windows account and removes them during uninstall. The ZIP and setup installer omit PDB debug symbols and LIB linker artifacts; all other published runtime files are retained.

The setup installer, registered portable copies, and MSIX manifest declare separate queue and playlist actions for supported audio files, along with file associations for Open with/default-app selection. Classic Windows shell verbs may appear under **Show more options** on Windows 11. The packaged actions are declared in the manifest but still need an MSIX package/install check. Windows requires the user to choose Music Player in Windows Settings before it can be the default player; installation does not change existing defaults.

When signing preflight passes, the workflow signs and verifies the executable, setup installers, and x64/ARM64 MSIX bundle. A stable release is blocked if preflight fails. Preview releases may contain unsigned ZIPs when it fails.

Signing does not remove every Windows warning. Windows SmartScreen may warn while a new publisher builds reputation, and third-party antivirus results cannot be guaranteed. Test signed installers and MSIX install, launch, upgrade and uninstall behavior on Windows before a stable release. Keep the format/device matrix pending until playback is checked on real audio files and devices.
