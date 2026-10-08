# GitHub release process

GitHub Releases is the distribution channel for Music Player. The Microsoft Store is not part of this release process. The executable uses the Music Player product name, and user data is stored under `%LOCALAPPDATA%\MusicPlayer`. The MSIX identity is `Kalabhaftu.MusicPlayer` with publisher `CN=Kalabhaftu`.

## Before merging changes to `main`

Open a pull request. `main` is configured to require the `Core tests · Linux`, `x64`, and `ARM64` checks. No reviewer approval is required by the branch rule. The Windows jobs build and smoke-start the x64 and native ARM64 portable apps; the Linux job runs the core suite.

## Private signing preflight

The Actions secrets must be named `MUSICPLAYER_SIGNING_PFX_BASE64` and `MUSICPLAYER_SIGNING_PFX_PASSWORD`. Configure both with a trusted Code Signing certificate whose subject is `CN=Kalabhaftu`; the previous secret names and certificate identity are obsolete. Secret presence is not proof the certificate is valid or trusted.

Use **Actions → Signed Windows release → Run workflow** on `main` to run the private preflight. A manual run checks the signing certificate's trust chain, validity period, Code Signing purpose, private key and exact `CN=Kalabhaftu` publisher. It signs and verifies a disposable executable copy and tests detached SHA-256 manifest signing, removes the temporary signing files, and does not build or publish release assets. The log reports only pass/fail and never prints secret values.

The latest recorded preflight ([run 37403559751](https://github.com/kalabhaftu/music-player/actions/runs/37403559751), October 6, 2026) failed during signing validation on `main`; its publish job was skipped, and it produced no release assets. That run used the previous package publisher and secret names, so it does not validate the current identity. Configure the current secrets and certificate, then run preflight again. The workflow logs the failing stage and exception type while suppressing certificate and password details. Do not describe signing as successful until a later preflight and packaged-artifact verification pass.

Preview and stable releases both fail closed unless the signing secrets are present and the certificate passes trust, identity, validity, Code Signing purpose, private-key, and disposable-signature checks. A failed or missing preflight creates no packages and publishes no GitHub Release. Do not use a self-signed certificate for ordinary public distribution.

## Tag a release

1. Update `CHANGELOG.md` with a heading matching the exact version.
2. Merge the tested pull request to `main`.
3. Create and push `vMAJOR.MINOR.PATCH` for stable, or the next unused `vMAJOR.MINOR.PATCH-preview.N` for a preview. The removed `v0.1.0-preview.1` identified the old UI; do not reuse it for the migration release.
4. Review the release workflow result and GitHub Release assets.

After preflight passes, the workflow creates self-contained portable ZIPs for x64 and ARM64. Each ZIP extracts into one folder named `MusicPlayer-VERSION-ARCHITECTURE-portable` and includes `MusicPlayer.exe`, `Portable-README.txt`, `LICENSE`, `ThirdPartyNotices.md`, `Register-MusicPlayer-FileActions.ps1`, and `Uninstall-MusicPlayer.ps1`. The portable ZIP itself is not Authenticode-signed; `MusicPlayer.exe` is signed and verified before it is archived. The README explains how to launch the app, register or remove the portable copy under Open with and in supported audio-file context menus, and that the library database, settings, artwork cache, and logs live in `%LOCALAPPDATA%\MusicPlayer`. The setup installer registers file actions for the installing Windows account and removes them during uninstall. Its uninstaller offers to remove the local index, playlists, settings, artwork cache, and logs; it never deletes music files. The ZIP and setup installer omit PDB debug symbols and LIB linker artifacts; all other published runtime files are retained.

The portable folder keeps the files needed to run without separately installed .NET or Windows App SDK dependencies. It uses the shared Microsoft Edge WebView2 Evergreen Runtime, which is not copied into each Music Player installation; users can install it from Microsoft's [WebView2 download page](https://developer.microsoft.com/microsoft-edge/webview2/). Setup embeds Microsoft's signed Evergreen bootstrapper and installs the runtime when needed. The WebUI source files are embedded in the app assembly and are not shipped as a loose `WebUI` folder. The package retains the managed runtime and WinUI binaries, VLC native codecs and plugins, application assets, and English resource satellites needed by the app. Native and VLC assets stay in loader-required runtime folders; root-level assemblies stay beside the executable for normal .NET probing. The exact file count varies by architecture and SDK version.

The setup installer is per-user and installs under the current user's Programs folder (normally `%LOCALAPPDATA%\Programs\Music Player`), so it does not need administrator permission. It registers file actions for that Windows account and removes them during uninstall. Setup asks whether to delete Music Player data, with keeping it as the default choice. The portable ZIP remains self-contained in one folder and needs no installer. Build outputs under `src\MusicPlayer.App\bin` are developer artifacts, not the release package layout; use the release workflow's staged portable folder or setup installer when inspecting the distribution layout.

The setup installer, registered portable copies, and MSIX manifest declare separate queue and playlist actions for supported audio files, along with file associations for Open with/default-app selection. Classic Windows shell verbs may appear under **Show more options** on Windows 11. The packaged actions are declared in the manifest but still need an MSIX package/install check. Windows requires the user to choose Music Player in Windows Settings before it can be the default player; installation does not change existing defaults.

When signing preflight passes, the workflow signs and verifies the executable inside each portable ZIP, each setup installer, and the x64/ARM64 MSIX bundle. It also creates `MusicPlayer-VERSION-SHA256SUMS.txt` for both portable ZIPs, both setup installers, the MSIX bundle, third-party notices, and the included verifier script; `MusicPlayer-VERSION-SHA256SUMS.txt.p7s` is a detached CMS signature created with that same release certificate. The signature is verified against the configured certificate and trusted chain before upload. The ZIP container is not Authenticode-signed, but its exact bytes are covered by this signed manifest. Preview and stable release workflows both stop before packaging if signing is not verified.

To verify a downloaded release, place all release assets in one directory and run this included script from that directory:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Verify-MusicPlayer-ReleaseManifest.ps1 -ManifestPath .\MusicPlayer-VERSION-SHA256SUMS.txt
```

The script verifies the detached signature, the trusted `CN=Kalabhaftu` Code Signing certificate and its online revocation status, then checks the SHA-256 hash and presence of every listed asset. It fails if any binary is missing or changed. The manifest signature is not timestamped, so certificate validity and trust must still pass when the manifest is verified.

Signing does not remove every Windows warning. Windows SmartScreen may warn while a new publisher builds reputation, and third-party antivirus results cannot be guaranteed. Test signed installers and MSIX install, launch, upgrade and uninstall behavior on Windows before a stable release. Keep the format/device matrix pending until playback is checked on real audio files and devices.
