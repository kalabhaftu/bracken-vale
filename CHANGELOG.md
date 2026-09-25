# Changelog

All notable changes to Bracken Vale are documented here.

## [Unreleased]

- Expose additional standard metadata fields and show them in track details.
- Preserve the current track while shuffling the upcoming queue, and honor repeat modes during timed crossfades.

## [0.1.0-preview.1]

- Add the initial native Windows music player, local library, playback, playlist, metadata and lyrics features.
- Add Windows CI and architecture-specific release packaging workflows.
- Log crashes, playback failures, and handled app errors locally; open the log folder from Settings.
- Remove tracks deleted from directories that were scanned successfully while retaining entries under offline or unreadable folders.
- Add dedicated album, artist, genre and folder browsing, plus queue reorder, removal and clear-upcoming controls.
- Fix scan exclusions with trailing separators and restore playback volume when pausing or cancelling a crossfade.
- Expose file path, last played and rating sorts in the library.
- Keep duplicate playlist entries distinct while navigating the playback queue.
- Identify failed tracks in the local log and recover the active playback state when LibVLC reports an error.
- Throttle failed automatic update checks to the configured weekly interval.
- Clear stale synced lyrics when playback moves before the next timed line.
- Refresh artwork cache keys after tag edits and update the current track details immediately.
- Publish self-contained x64 and ARM64 portable ZIPs from successful Windows CI runs.
- Reset artwork accents when switching back to native styling and recover from damaged EQ settings.
- Edit and inspect Xiph, ID3v2, ASF and APEv2 custom text tags; preserve each format's own field names.
- Report embedded lyrics as saved when only the library refresh fails.
- Exclude system folders by their drive-root location while keeping user folders with the same name.
- Keep LRCLIB search and queue, equalizer and update feedback inside their open dialogs.
- Roll back unsaved navigation changes when Settings is cancelled or contains an invalid folder path.
- Show app feedback in dismissible banners so playback errors cannot collide with open dialogs.
- Remove the selected duplicate playlist entry instead of always removing its first occurrence.
- Honor the weekly update-check interval when saving Settings while keeping manual checks immediate.
- Display plain lyrics in Now Playing when timed LRC lines are unavailable.
- Prevent an early sort-selection event from crashing the window during XAML initialization.
