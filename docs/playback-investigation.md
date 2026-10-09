# Playback stopping and queue advancement investigation

Investigated on 2026-10-09 against the local Windows logs, native LibVLC playback, and commits `1663ace`, `36e7643`, and `1ba318d`.

## Root cause

The app log repeatedly recorded `LibVLC end event ignored: event came from inactive player` while the current song was ending. `PlaybackService` compared the event's `sender` directly with its active `MediaPlayer`. LibVLCSharp actually passes its internal `MediaPlayerEventManager` as sender. Consequently every real end callback was rejected, `TrackEnded` was never raised, and the queue never received automatic advancement. Error callbacks used the same incorrect comparison.

The binding's behavior is visible in [VideoLAN's event manager source](https://github.com/videolan/libvlcsharp/blob/3.x/src/LibVLCSharp/Shared/Events/MediaPlayerEventManager.cs).

The prior fixes changed state checks and dispatcher scheduling but retained the sender comparison. Their core policy test supplied booleans directly, so it never exercised the actual native event sender and could not detect this failure.

## Seeking failures

`1ba318d` reloaded an ended song using `LoadPaused`, then returned without starting it. Dragging the progress slider back therefore prepared a paused song instead of resuming playback. Separately, seeking a restored, unstarted session cleared `_restorePosition` and assigned native `Time` before an input existed; the requested position was lost. Deferred seeking also checked only media length, which does not establish that the input can accept a seek.

## Correction

- Subscriptions capture the actual owning player and its media generation, rather than using the native event sender.
- Native callbacks queue their handling without taking the service lock or issuing playback commands. Queued events from replaced media are rejected; duplicate end notifications are suppressed.
- End/error callbacks are authoritative for their media generation. Reading native `State` later loses errors when VLC has already stopped.
- Seeking an ended song reloads and resumes at the requested position. Seeking a paused or restored song preserves pause and retains the new position until the decoder is seekable. Resume also resets ended media correctly.
- Canceling or failing a crossfade rebinds events to the surviving outgoing media, allowing its natural end to advance the queue. A successful promotion clears ended state for the incoming track.
- Seeking after the final queue entry resumes Windows media controls and listening tracking as well as the engine.

## Validation

The first three native regression tests failed against the previous implementation: queue progression timed out, seeking after end did not resume, and restored seeking returned position zero instead of the requested 1000 ms.

`tests/MusicPlayer.Playback.Tests` links the actual app playback service and queue coordinators, loads the shipped LibVLC packages, and generates silent WAV fixtures. Its dummy audio output allows real native event/decoder tests without an audio device. Windows x64 CI runs this suite before packaging. Tests cover queue advancement and final stop, seeking after natural end and after queue stop, restored and paused seeking, repeat, crossfade promotion, failed crossfade recovery, and native file-open errors.

All 9 native playback cases and the 62 existing core tests passed locally. The x64 WinUI build passed with zero warnings and zero errors. Native tests do not establish that every supported media format, physical output device, or WebView interaction works; those remain separate Windows validation tasks in the project status tracker.
