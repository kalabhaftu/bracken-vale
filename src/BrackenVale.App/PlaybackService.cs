using BrackenVale.Core;
using LibVLCSharp.Shared;

namespace BrackenVale.App;

public sealed class PlaybackService : IDisposable
{
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _first;
    private readonly MediaPlayer _second;
    private MediaPlayer _active;
    private MediaPlayer _next;
    private Media? _activeMedia;
    private Media? _nextMedia;
    private CancellationTokenSource? _crossfadeCancellation;
    private float _volume = 75;
    private long _restorePosition;

    public PlaybackService()
    {
        LibVLCSharp.Shared.Core.Initialize();
        _libVlc = new LibVLC();
        _first = new(_libVlc); _second = new(_libVlc); _active = _first; _next = _second;
        _first.EndReached += EndReached; _second.EndReached += EndReached;
    }

    public event EventHandler? TrackEnded;
    public event Action<Track>? CrossfadeCompleted;
    public Track? CurrentTrack { get; private set; }
    public bool IsPlaying => _active.IsPlaying;
    public long Position => _restorePosition > _active.Time ? _restorePosition : _active.Time;
    public long Duration => _active.Length;
    public int Volume { get => (int)_volume; set { _volume = Math.Clamp(value, 0, 100); _active.Volume = (int)_volume; _next.Volume = (int)_volume; } }

    public void LoadPaused(Track track, long positionMilliseconds)
    {
        SetMedia(_active, ref _activeMedia, track.Path);
        _active.Time = Math.Max(0, positionMilliseconds);
        _restorePosition = Math.Max(0, positionMilliseconds);
        CurrentTrack = track;
    }

    public void Play(Track track)
    {
        CancelCrossfade();
        _active.Stop();
        SetMedia(_active, ref _activeMedia, track.Path);
        _restorePosition = 0;
        CurrentTrack = track;
        _active.Volume = (int)_volume;
        _active.Play();
    }

    public void PlayLoaded()
    {
        if (_active.Media is null) return;
        _active.Play();
        if (_restorePosition > 0) _ = SeekAfterStartAsync(_active, _restorePosition);
    }

    public void Pause() => _active.Pause();
    public void Stop() { CancelCrossfade(); _active.Stop(); }
    public void Seek(long positionMilliseconds) { _restorePosition = 0; _active.Time = Math.Max(0, positionMilliseconds); }

    private async Task SeekAfterStartAsync(MediaPlayer player, long position)
    {
        try
        {
            for (var attempt = 0; attempt < 20 && ReferenceEquals(player, _active); attempt++)
            {
                await Task.Delay(100).ConfigureAwait(false);
                if (player.Length > 0) { player.Time = Math.Min(position, player.Length); _restorePosition = 0; return; }
            }
        }
        catch (ObjectDisposedException) { }
    }

    public void ApplyEqualizer(string? preset, IReadOnlyList<float>? bands = null)
    {
        if (string.IsNullOrWhiteSpace(preset) && bands is null) { _active.UnsetEqualizer(); _next.UnsetEqualizer(); return; }
        using var current = new Equalizer();
        using var equalizer = preset is not null ? new Equalizer((uint)Math.Clamp(PresetIndex(preset, current), 0, (int)current.PresetCount - 1)) : new Equalizer();
        if (bands is not null)
            for (var i = 0; i < Math.Min(bands.Count, (int)equalizer.BandCount); i++) equalizer.SetAmp(bands[i], (uint)i);
        _active.SetEqualizer(equalizer); _next.SetEqualizer(equalizer);
    }

    public async Task CrossfadeToAsync(Track nextTrack, int milliseconds, CancellationToken cancellationToken = default)
    {
        if (milliseconds <= 0 || CurrentTrack is null) { Play(nextTrack); return; }
        CancelCrossfade();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _crossfadeCancellation = cts;
        var old = _active; var incoming = _next;
        incoming.Stop(); SetMedia(incoming, ref _nextMedia, nextTrack.Path); incoming.Volume = 0; incoming.Play();
        var steps = Math.Max(1, milliseconds / 40);
        try
        {
            for (var step = 1; step <= steps; step++)
            {
                await Task.Delay(40, cts.Token).ConfigureAwait(false);
                var fraction = step / (float)steps;
                incoming.Volume = (int)(_volume * fraction);
                old.Volume = (int)(_volume * (1 - fraction));
            }
            old.Stop();
            if (ReferenceEquals(old, _first)) { _active = _second; _next = _first; (_activeMedia, _nextMedia) = (_nextMedia, _activeMedia); }
            else { _active = _first; _next = _second; (_activeMedia, _nextMedia) = (_nextMedia, _activeMedia); }
            CurrentTrack = nextTrack;
            _active.Volume = (int)_volume;
            CrossfadeCompleted?.Invoke(nextTrack);
        }
        finally
        {
            if (ReferenceEquals(_crossfadeCancellation, cts)) _crossfadeCancellation = null;
            cts.Dispose();
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

    private void SetMedia(MediaPlayer player, ref Media? media, string path)
    {
        media?.Dispose();
        media = new Media(_libVlc, new Uri(path));
        player.Media = media;
    }

    private void EndReached(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, _active) && _crossfadeCancellation is null) TrackEnded?.Invoke(this, EventArgs.Empty);
    }

    private void CancelCrossfade()
    {
        _crossfadeCancellation?.Cancel();
        _next.Stop();
        _next.Volume = (int)_volume;
    }

    public void Dispose()
    {
        CancelCrossfade(); _active.Stop();
        _activeMedia?.Dispose(); _nextMedia?.Dispose();
        _first.Dispose(); _second.Dispose(); _libVlc.Dispose();
    }
}
