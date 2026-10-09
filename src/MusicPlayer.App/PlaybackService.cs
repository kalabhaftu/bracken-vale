using MusicPlayer.Core;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;

namespace MusicPlayer.App;

public sealed record VideoSubtitleOption(int Id, string Name);

/// <summary>Serializes LibVLC state changes and creates the crossfade player only when it is first needed.</summary>
public sealed class PlaybackService : IDisposable
{
    private readonly object _gate = new();
    private readonly LibVLC _libVlc;
    private HashSet<string> _videoExtensions = new(StringComparer.OrdinalIgnoreCase);
    private readonly MediaPlayer _first;
    private MediaPlayer _active;
    private MediaPlayer? _spare;
    private Media? _activeMedia;
    private Media? _spareMedia;
    private Track? _currentTrack;
    private Track? _crossfadeTarget;
    private CancellationTokenSource? _crossfadeCancellation;
    private string? _reportedFailurePath;
    private string? _audioOutputDeviceId;
    private string? _equalizerPreset;
    private IReadOnlyList<float>? _equalizerBands;
    private float _volume = 75;
    private float _videoPlaybackRate = 1f;
    private long _restorePosition;
    private long _mediaGeneration;
    private long _handledEndReachedGeneration = -1;
    private bool _trackEnded;
    private bool _videoTrack;
    private bool _disposed;

    public PlaybackService()
    {
        LibVLCSharp.Shared.Core.Initialize();
        // Video is rendered into the app's own child HWND when an enabled video
        // extension is played. Never let LibVLC create its standalone video window.
        _libVlc = new LibVLC("--aout=mmdevice", "--no-video-title-show");
        _libVlc.Log += (_, args) =>
        {
            if (args.Level is LogLevel.Warning or LogLevel.Error)
                LocalAppLog.Shared.Warning("libvlc", args.FormattedLog);
        };
        _first = new(_libVlc);
        _active = _first;
        Subscribe(_first);
    }

    public event EventHandler? TrackEnded;
    public event Action<Track>? CrossfadeCompleted;
    public event Action<Track>? CrossfadeFailed;
    public event Action<Track>? PlaybackFailed;
    public Func<Track, nint>? VideoSurfaceRequested { get; set; }

    public Track? CurrentTrack { get { lock (_gate) return _currentTrack; } }
    public bool IsPlaying { get { lock (_gate) return !_disposed && _active.IsPlaying; } }
    public bool HasEnded { get { lock (_gate) return !_disposed && _trackEnded; } }
    public bool IsVideoMode { get { lock (_gate) return !_disposed && _videoTrack; } }
    public long Position { get { lock (_gate) return _disposed ? 0 : _restorePosition > _active.Time ? _restorePosition : _active.Time; } }
    public long Duration
    {
        get
        {
            lock (_gate)
            {
                if (_disposed) return 0;
                var engineDuration = _active.Length;
                if (engineDuration > 0) return engineDuration;
                return Math.Max(0, (long)(_currentTrack?.Duration.TotalMilliseconds ?? 0));
            }
        }
    }
    public int Volume
    {
        get { lock (_gate) return (int)_volume; }
        set
        {
            lock (_gate)
            {
                if (_disposed) return;
                _volume = Math.Clamp(value, 0, 100);
                _active.Volume = (int)_volume;
                if (_spare is not null) _spare.Volume = (int)_volume;
            }
        }
    }

    public float VideoPlaybackRate { get { lock (_gate) return _videoPlaybackRate; } }
    public int VideoSubtitleId { get { lock (_gate) return _disposed || !_videoTrack ? -1 : _active.Spu; } }

    public bool SetVideoPlaybackRate(float rate)
    {
        rate = Math.Clamp(rate, .5f, 2f);
        lock (_gate)
        {
            if (_disposed || !_videoTrack) return false;
            if (_active.SetRate(rate) != 0) return false;
            _videoPlaybackRate = rate;
            return true;
        }
    }

    public IReadOnlyList<VideoSubtitleOption> GetVideoSubtitleOptions()
    {
        lock (_gate)
        {
            if (_disposed || !_videoTrack) return [];
            try
            {
                return _active.SpuDescription?
                    .Select(track => new VideoSubtitleOption(track.Id, string.IsNullOrWhiteSpace(track.Name) ? $"Subtitle {track.Id}" : track.Name))
                    .ToArray() ?? [];
            }
            catch (InvalidOperationException) { return []; }
        }
    }

    public bool SetVideoSubtitle(int trackId)
    {
        lock (_gate) return !_disposed && _videoTrack && _active.SetSpu(trackId);
    }

    public bool TakeVideoSnapshot(string path)
    {
        lock (_gate)
        {
            if (_disposed || !_videoTrack) return false;
            return _active.TakeSnapshot(0, path, 0, 0);
        }
    }

    public void SetVideoExtensions(IEnumerable<string> extensions)
    {
        lock (_gate)
            _videoExtensions = extensions.Select(extension => extension.StartsWith('.') ? extension : $".{extension}")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public void SetVideoSurfaceHandle(nint handle)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_videoTrack) _active.Hwnd = handle;
        }
    }

    private bool IsVideoPathCore(string path) => _videoExtensions.Contains(Path.GetExtension(path));

    private nint RequestVideoSurface(Track track)
    {
        Func<Track, nint>? request;
        lock (_gate)
        {
            if (_disposed || !IsVideoPathCore(track.Path)) return 0;
            request = VideoSurfaceRequested;
        }
        return request?.Invoke(track) ?? 0;
    }

    public void SelectAudioOutputDevice(string? deviceId)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _audioOutputDeviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
            ApplyAudioOutputDevice(_active);
            if (_spare is not null) ApplyAudioOutputDevice(_spare);
        }
    }

    public void LoadPaused(Track track, long positionMilliseconds)
    {
        var requestedSurface = RequestVideoSurface(track);
        lock (_gate)
        {
            if (_disposed) return;
            CancelCrossfadeCore();
            _mediaGeneration++;
            _trackEnded = false;
            _active.Stop();
            _videoTrack = IsVideoPathCore(track.Path);
            if (_videoTrack && requestedSurface == 0)
            {
                _currentTrack = track;
                ReportPlaybackFailureCore(track);
                return;
            }
            if (_videoTrack)
            {
                _active.Hwnd = requestedSurface;
            }
            SetMedia(_active, ref _activeMedia, track.Path);
            ApplyAudioOutputDevice(_active);
            _active.Time = Math.Max(0, positionMilliseconds);
            _restorePosition = Math.Max(0, positionMilliseconds);
            _currentTrack = track;
            _reportedFailurePath = null;
        }
    }

    public void UpdateTrackMetadata(Track track)
    {
        lock (_gate)
            if (!_disposed && _currentTrack is { } current && string.Equals(current.Path, track.Path, StringComparison.OrdinalIgnoreCase))
                _currentTrack = track;
    }

    public void Play(Track track)
    {
        var requestedSurface = RequestVideoSurface(track);
        lock (_gate)
        {
            if (_disposed) return;
            CancelCrossfadeCore();
            _mediaGeneration++;
            _trackEnded = false;
            _active.Stop();
            _videoTrack = IsVideoPathCore(track.Path);
            if (_videoTrack && requestedSurface == 0)
            {
                _currentTrack = track;
                ReportPlaybackFailureCore(track);
                return;
            }
            if (_videoTrack)
            {
                _active.Hwnd = requestedSurface;
            }
            SetMedia(_active, ref _activeMedia, track.Path);
            ApplyAudioOutputDevice(_active);
            _restorePosition = 0;
            _currentTrack = track;
            _reportedFailurePath = null;
            _active.Volume = (int)_volume;
            if (!_active.Play()) ReportPlaybackFailureCore(track);
        }
    }

    public void PlayLoaded()
    {
        Track? resumeVideoTrack;
        lock (_gate)
        {
            if (_disposed) return;
            resumeVideoTrack = _videoTrack ? _currentTrack : null;
        }
        var requestedSurface = resumeVideoTrack is null ? 0 : RequestVideoSurface(resumeVideoTrack);
        MediaPlayer player;
        long generation;
        long restorePosition;
        lock (_gate)
        {
            if (_disposed) return;
            if (_activeMedia is null) return;
            if (_videoTrack)
            {
                if (requestedSurface == 0) { ReportPlaybackFailureCore(_currentTrack); return; }
                _active.Hwnd = requestedSurface;
            }
            ApplyAudioOutputDevice(_active);
            _reportedFailurePath = null;
            if (!_active.Play()) { ReportPlaybackFailureCore(_currentTrack); return; }
            player = _active;
            generation = _mediaGeneration;
            restorePosition = _restorePosition;
        }
        if (restorePosition > 0) _ = SeekAfterStartAsync(player, generation, restorePosition);
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_disposed) return;
            CancelCrossfadeCore();
            _active.Pause();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_disposed) return;
            CancelCrossfadeCore();
            _mediaGeneration++;
            _restorePosition = 0;
            _active.Stop();
        }
    }

    public void CancelCrossfade()
    {
        lock (_gate)
            if (!_disposed) CancelCrossfadeCore();
    }

    public void Seek(long positionMilliseconds)
    {
        lock (_gate)
        {
            if (_disposed) return;
            CancelCrossfadeCore();
            _mediaGeneration++;
            _trackEnded = false;
            _restorePosition = 0;
            var duration = _active.Length;
            _active.Time = Math.Clamp(positionMilliseconds, 0, duration > 0 ? duration : long.MaxValue);
        }
    }

    private async Task SeekAfterStartAsync(MediaPlayer player, long generation, long position)
    {
        try
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(100).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_disposed || generation != _mediaGeneration || !ReferenceEquals(player, _active)) return;
                    if (player.Length > 0)
                    {
                        player.Time = Math.Min(position, player.Length);
                        _restorePosition = 0;
                        return;
                    }
                }
            }
        }
        catch (ObjectDisposedException) { }
    }

    public void ApplyEqualizer(string? preset, IReadOnlyList<float>? bands = null)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _equalizerPreset = string.IsNullOrWhiteSpace(preset) ? null : preset;
            _equalizerBands = bands?.ToArray();
            ApplyEqualizerCore(_active);
            if (_spare is not null) ApplyEqualizerCore(_spare);
        }
    }

    public async Task CrossfadeToAsync(Track nextTrack, int milliseconds, CancellationToken cancellationToken = default)
    {
        bool involvesVideo;
        lock (_gate) involvesVideo = _videoTrack || IsVideoPathCore(nextTrack.Path);
        if (involvesVideo) { Play(nextTrack); return; }
        if (milliseconds <= 0) { Play(nextTrack); return; }
        cancellationToken.ThrowIfCancellationRequested();

        MediaPlayer old = _active;
        MediaPlayer incoming = _active;
        CancellationTokenSource? fade = null;
        var fadeMilliseconds = 0;
        try
        {
            lock (_gate)
            {
                if (_disposed) return;
                if (_currentTrack is not null)
                {
                    CancelCrossfadeCore();
                    old = _active;
                    var remaining = _active.Length > 0 ? Math.Max(0, _active.Length - _active.Time) : milliseconds;
                    fadeMilliseconds = Math.Min(milliseconds, (int)Math.Min(int.MaxValue, remaining));
                    if (fadeMilliseconds > 0)
                    {
                        incoming = EnsureSpareCore();
                        fade = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        _crossfadeCancellation = fade;
                        _crossfadeTarget = nextTrack;
                        _mediaGeneration++;
                        incoming.Stop();
                        SetMedia(incoming, ref _spareMedia, nextTrack.Path);
                        ApplyAudioOutputDevice(incoming);
                        incoming.Volume = 0;
                        if (!incoming.Play()) throw new InvalidOperationException($"LibVLC refused to play '{nextTrack.Path}'. {_libVlc.LastLibVLCError}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
                if (!_disposed) CancelCrossfadeCore();
            LogCrossfadeFailure(nextTrack, ex);
            CrossfadeFailed?.Invoke(nextTrack);
            fade?.Dispose();
            return;
        }

        if (fadeMilliseconds <= 0) { Play(nextTrack); fade?.Dispose(); return; }
        var tokenSource = fade!;
        var stepCount = Math.Max(1, (int)Math.Ceiling(fadeMilliseconds / 40d));
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            for (var step = 1; step <= stepCount; step++)
            {
                await Task.Delay(40, tokenSource.Token).ConfigureAwait(false);
                Track? completed = null;
                lock (_gate)
                {
                    if (_disposed || !ReferenceEquals(_crossfadeCancellation, tokenSource) || tokenSource.IsCancellationRequested) return;
                    var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    var fraction = Math.Clamp(elapsed / fadeMilliseconds, 0, 1);
                    incoming.Volume = (int)(_volume * fraction);
                    old.Volume = (int)(_volume * (1 - fraction));
                    if (fraction >= 1 || step == stepCount)
                    {
                        old.Stop();
                        _active = incoming;
                        _spare = old;
                        (_activeMedia, _spareMedia) = (_spareMedia, _activeMedia);
                        _currentTrack = nextTrack;
                        _restorePosition = 0;
                        _active.Volume = (int)_volume;
                        _spare.Volume = (int)_volume;
                        _crossfadeTarget = null;
                        _crossfadeCancellation = null;
                        completed = nextTrack;
                    }
                }
                if (completed is not null)
                {
                    CrossfadeCompleted?.Invoke(completed);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (tokenSource.IsCancellationRequested) { }
        catch (Exception ex)
        {
            lock (_gate)
                if (ReferenceEquals(_crossfadeCancellation, tokenSource)) CancelCrossfadeCore();
            LogCrossfadeFailure(nextTrack, ex);
            CrossfadeFailed?.Invoke(nextTrack);
        }
        finally
        {
            lock (_gate)
                if (ReferenceEquals(_crossfadeCancellation, tokenSource)) CancelCrossfadeCore();
            tokenSource.Dispose();
        }
    }

    public static IReadOnlyList<string> EqualizerPresets()
    {
        using var equalizer = new Equalizer();
        return Enumerable.Range(0, (int)equalizer.PresetCount).Select(i => equalizer.PresetName((uint)i) ?? $"Preset {i + 1}").ToArray();
    }

    public static IReadOnlyList<string> EqualizerBands()
    {
        using var equalizer = new Equalizer();
        return Enumerable.Range(0, (int)equalizer.BandCount).Select(i => equalizer.BandFrequency((uint)i) >= 1000
            ? $"{equalizer.BandFrequency((uint)i) / 1000:0.#} kHz" : $"{equalizer.BandFrequency((uint)i):0} Hz").ToArray();
    }

    public static IReadOnlyList<float> EqualizerPresetBands(string name)
    {
        using var equalizer = new Equalizer(); var presetIndex = PresetIndex(name, equalizer);
        using var preset = new Equalizer((uint)presetIndex);
        return Enumerable.Range(0, (int)preset.BandCount).Select(i => preset.Amp((uint)i)).ToArray();
    }

    private static int PresetIndex(string name, Equalizer equalizer)
    {
        for (var i = 0; i < equalizer.PresetCount; i++) if (string.Equals(equalizer.PresetName((uint)i), name, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

    private MediaPlayer EnsureSpareCore()
    {
        if (_spare is not null) return _spare;
        _spare = new MediaPlayer(_libVlc);
        Subscribe(_spare);
        _spare.Volume = (int)_volume;
        ApplyAudioOutputDevice(_spare);
        ApplyEqualizerCore(_spare);
        return _spare;
    }

    private void ApplyEqualizerCore(MediaPlayer player)
    {
        if (_equalizerPreset is null && _equalizerBands is null) { player.UnsetEqualizer(); return; }
        using var current = new Equalizer();
        using var equalizer = _equalizerPreset is not null
            ? new Equalizer((uint)Math.Clamp(PresetIndex(_equalizerPreset, current), 0, (int)current.PresetCount - 1))
            : new Equalizer();
        if (_equalizerBands is not null)
            for (var i = 0; i < Math.Min(_equalizerBands.Count, (int)equalizer.BandCount); i++) equalizer.SetAmp(_equalizerBands[i], (uint)i);
        player.SetEqualizer(equalizer);
    }

    private void SetMedia(MediaPlayer player, ref Media? media, string path)
    {
        media?.Dispose();
        media = new Media(_libVlc, new Uri(path));
        if (ReferenceEquals(player, _active) && _videoTrack)
        {
            foreach (var subtitlePath in FindNearbySubtitleFiles(path))
            {
                try
                {
                    if (!media.AddSlave(MediaSlaveType.Subtitle, 4, new Uri(subtitlePath)))
                        LocalAppLog.Shared.Warning("video-subtitle", $"LibVLC could not add the nearby subtitle '{Path.GetFileName(subtitlePath)}'.");
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
                {
                    LocalAppLog.Shared.Warning("video-subtitle", $"Could not add the nearby subtitle '{Path.GetFileName(subtitlePath)}'.", ex);
                }
            }
        }
        var requestedRate = ReferenceEquals(player, _active) && _videoTrack ? _videoPlaybackRate : 1f;
        player.Media = media;
        if (player.SetRate(requestedRate) != 0 && requestedRate != 1f)
            LocalAppLog.Shared.Warning("video-playback", "The media engine rejected the requested video playback speed.");
    }

    private static string[] FindNearbySubtitleFiles(string videoPath)
    {
        var directory = Path.GetDirectoryName(videoPath);
        var mediaName = Path.GetFileNameWithoutExtension(videoPath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(mediaName) || !Directory.Exists(directory)) return [];
        var subtitleExtensions = new HashSet<string>([".srt", ".ass", ".ssa", ".vtt"], StringComparer.OrdinalIgnoreCase);
        try
        {
            return Directory.EnumerateFiles(directory)
                .Where(candidate =>
                {
                    var extension = Path.GetExtension(candidate);
                    if (!subtitleExtensions.Contains(extension)) return false;
                    var subtitleName = Path.GetFileNameWithoutExtension(candidate);
                    return subtitleName.Equals(mediaName, StringComparison.OrdinalIgnoreCase) ||
                        subtitleName.StartsWith(mediaName + ".", StringComparison.OrdinalIgnoreCase);
                })
                .OrderBy(candidate => Path.GetFileNameWithoutExtension(candidate).Equals(mediaName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(candidate => candidate, StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            LocalAppLog.Shared.Warning("video-subtitle", "Could not look for subtitle files next to the video.", ex);
            return [];
        }
    }

    private void ApplyAudioOutputDevice(MediaPlayer player)
    {
        var deviceId = _audioOutputDeviceId ?? string.Empty;
        player.SetOutputDevice(deviceId, "mmdevice");
        player.SetOutputDevice(deviceId);
    }

    private void Subscribe(MediaPlayer player)
    {
        player.EndReached += EndReached;
        player.EncounteredError += EncounteredError;
    }

    private void EndReached(object? sender, EventArgs e)
    {
        bool ended = false;
        lock (_gate)
        {
            var generation = _mediaGeneration;
            var length = _active.Length;
            var position = _active.Time;
            var endTolerance = Math.Clamp(length / 100, 250, 1500);
            var reachedNaturalEnd = _active.State == VLCState.Ended || (length > 0 && position >= length - endTolerance);
            if (_handledEndReachedGeneration != generation && PlaybackEventPolicy.ShouldHandleEndReached(
                    ReferenceEquals(sender, _active), _currentTrack is not null, reachedNaturalEnd,
                    _crossfadeCancellation is not null))
            {
                _handledEndReachedGeneration = generation;
                _trackEnded = true;
                ended = true;
            }
        }
        if (ended) TrackEnded?.Invoke(this, EventArgs.Empty);
    }

    private void EncounteredError(object? sender, EventArgs e)
    {
        Track? activeFailure = null;
        Track? fadeFailure = null;
        lock (_gate)
        {
            if (_disposed) return;
            if (PlaybackEventPolicy.ShouldHandleActiveError(
                    ReferenceEquals(sender, _active),
                    _currentTrack is not null,
                    _active.State == VLCState.Error))
            {
                if (_currentTrack is { } current && !string.Equals(_reportedFailurePath, current.Path, StringComparison.OrdinalIgnoreCase))
                {
                    _reportedFailurePath = current.Path;
                    activeFailure = current;
                }
            }
            else if (PlaybackEventPolicy.ShouldHandleCrossfadeError(
                         ReferenceEquals(sender, _spare),
                         _crossfadeTarget is not null,
                         _spare?.State == VLCState.Error))
            {
                fadeFailure = _crossfadeTarget;
                CancelCrossfadeCore();
            }
        }
        if (activeFailure is not null) ReportPlaybackFailure(activeFailure);
        if (fadeFailure is not null)
        {
            LogCrossfadeFailure(fadeFailure, new InvalidOperationException(_libVlc.LastLibVLCError));
            CrossfadeFailed?.Invoke(fadeFailure);
        }
    }

    private void ReportPlaybackFailureCore(Track? track)
    {
        if (track is null || string.Equals(_reportedFailurePath, track.Path, StringComparison.OrdinalIgnoreCase)) return;
        _reportedFailurePath = track.Path;
        LocalAppLog.Shared.Warning("playback", $"LibVLC could not open or decode '{track.Path}'. {_libVlc.LastLibVLCError}");
        PlaybackFailed?.Invoke(track);
    }

    private void ReportPlaybackFailure(Track track)
    {
        LocalAppLog.Shared.Warning("playback", $"LibVLC could not open or decode '{track.Path}'. {_libVlc.LastLibVLCError}");
        PlaybackFailed?.Invoke(track);
    }

    private static void LogCrossfadeFailure(Track track, Exception exception)
    {
        LocalAppLog.Shared.Error("crossfade", $"Could not crossfade to '{track.Path}'.", exception);
    }

    private void CancelCrossfadeCore()
    {
        var fade = _crossfadeCancellation;
        _crossfadeCancellation = null;
        _crossfadeTarget = null;
        fade?.Cancel();
        if (_disposed) return;
        if (_spare is not null)
        {
            _spare.Stop();
            _spare.Volume = (int)_volume;
        }
        _active.Volume = (int)_volume;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            CancelCrossfadeCore();
            _disposed = true;
            _active.Stop();
            _active.EndReached -= EndReached;
            _active.EncounteredError -= EncounteredError;
            _activeMedia?.Dispose();
            _spareMedia?.Dispose();
            _active.Dispose();
            if (_spare is not null)
            {
                _spare.EndReached -= EndReached;
                _spare.EncounteredError -= EncounteredError;
                _spare.Dispose();
            }
            _libVlc.Dispose();
        }
    }
}
