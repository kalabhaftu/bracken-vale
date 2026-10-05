# Known limitations

Updated: 2026-10-04. See [project status](project-status.md) for the implementation and verification checklist.

- The audio formats listed in [the format matrix](format-matrix.md) are recognized by the scanner, but no format is marked playback-verified until checked on Windows with actual files and devices.
- A local WinUI build needs the .NET 10 SDK, Windows SDK 10.0.26100 or later, and a successful NuGet restore. Package downloads timed out and NuGet name resolution failed on the development workstation during this pass. The PR's Windows x64 and native ARM64 CI jobs did build, publish and startup-smoke the app; interactive playback and UI behavior still need manual Windows checks.
- The signing secrets' presence does not establish certificate trust or signing readiness. Stable release tags remain blocked until the private signing preflight passes. Preview ZIPs may be unsigned.
- A trusted signature does not guarantee that SmartScreen warnings disappear immediately or that all third-party antivirus products report the same result.
- The app is local-first. LRCLIB lyrics search and optional GitHub release checks are the only network features; lyric lookup requires a user action and sends title and artist only.
