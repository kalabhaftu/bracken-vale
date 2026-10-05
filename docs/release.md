# GitHub release process

GitHub Releases is Bracken Vale's distribution channel. The Microsoft Store is not part of this release process.

## Before merging changes to `main`

Open a pull request. `main` is configured to require the `Core tests · Linux`, `x64`, and `ARM64` checks. No reviewer approval is required by the branch rule. The Windows jobs build and smoke-start the x64 and native ARM64 portable apps; the Linux job runs the core suite.

## Private signing preflight

The Actions secrets are named `BRACKENVALE_SIGNING_PFX_BASE64` and `BRACKENVALE_SIGNING_PFX_PASSWORD`. Secret presence is not proof the certificate is valid or trusted.

Use **Actions → Signed Windows release → Run workflow** on `main` to run the private preflight. A manual run checks the signing certificate's trust chain, validity period, Code Signing purpose, private key and exact `CN=Bracken Vale` publisher. It signs and verifies a disposable text file, removes the temporary signing files, and does not build or publish release assets. The log reports only pass/fail and never prints secret values.

If the preflight fails, unsigned previews remain available as portable ZIPs. Stable tags fail before packaging until the repository has a publicly trusted, usable certificate whose publisher matches the MSIX identity. Do not use a self-signed certificate for ordinary public distribution.

## Tag a release

1. Update `CHANGELOG.md` with a heading matching the exact version.
2. Merge the tested pull request to `main`.
3. Create and push `vMAJOR.MINOR.PATCH` for stable, or `vMAJOR.MINOR.PATCH-preview.N` for a preview.
4. Review the release workflow result and GitHub Release assets.

The workflow creates self-contained portable ZIPs for x64 and ARM64. When signing preflight passes, it also signs and verifies the setup installers and x64/ARM64 MSIX bundle. A stable release is blocked if preflight fails. Preview releases may contain unsigned ZIPs when it fails.

Signing does not remove every Windows warning. Windows SmartScreen may warn while a new publisher builds reputation, and third-party antivirus results cannot be guaranteed. Test signed installers and MSIX install, launch, upgrade and uninstall behavior on Windows before a stable release. Keep the format/device matrix pending until playback is checked on real audio files and devices.
