# Known limitations

Updated: 2026-10-04. See [project status](project-status.md) for the implementation and verification checklist.

- The audio formats listed in [the format matrix](format-matrix.md) are recognized by the scanner, but no format is marked playback-verified until checked on Windows with actual files and devices.
- A WinUI build needs the .NET 10 SDK, Windows SDK 10.0.26100 or later, and a successful NuGet restore. During this implementation pass, Windows App SDK and LibVLC package downloads timed out and NuGet name resolution failed, so the source app could not be built or launched here.
- The signing secrets' presence does not establish certificate trust or signing readiness. Stable release tags remain blocked until the private signing preflight passes. Preview ZIPs may be unsigned.
- A trusted signature does not guarantee that SmartScreen warnings disappear immediately or that all third-party antivirus products report the same result.
- The app is local-first. LRCLIB lyrics search and optional GitHub release checks are the only network features; lyric lookup requires a user action and sends title and artist only.
