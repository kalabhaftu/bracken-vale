using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BrackenVale.Core;
using Microsoft.UI.Xaml;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace BrackenVale.App;

public sealed partial class MainWindow
{
    private void PublishPlaybackState()
    {
        var current = _playback.CurrentTrack;
        _webBridge?.SendEvent("playbackStateChanged", new
        {
            playing = _playback.IsPlaying, positionSeconds = Math.Max(0, _playback.Position) / 1000d,
            durationSeconds = Math.Max(0, _playback.Duration) / 1000d, volume = _playback.Volume,
            shuffle = _shuffle, repeat = _repeatMode, repeatA = _repeatA?.TotalSeconds, repeatB = _repeatB?.TotalSeconds,
            queueIndex = _queueIndex, trackId = current is null ? null : _libraryQueries.TrackId(current.Path)
        });
    }

    private void PublishQueueState(bool force = false)
    {
        var signature = $"{_queueIndex}:{string.Join(';', _queue.Select(_libraryQueries.OpaqueId))}";
        if (!force && signature == _lastWebQueueSignature) return;
        _lastWebQueueSignature = signature;
        _webBridge?.SendEvent("queueChanged", new { entries = QueueDtos(Math.Max(0, _queueIndex), 30), queueOffset = Math.Max(0, _queueIndex), queueIndex = _queueIndex, totalCount = _queue.Count });
    }

    private object ScanDto()
    {
        var (active, paused, cancelling) = _libraryScan.GetStatus();
        var outcomeVisible = !active && _scanOutcomeUtc is { } finishedAt && DateTimeOffset.UtcNow - finishedAt < TimeSpan.FromSeconds(8);
        return new
        {
            active,
            paused,
            cancelling,
            filesFound = _scanFilesFound,
            directoriesVisited = _scanDirectoriesVisited,
            currentPath = active ? _scanCurrentPath : null,
            outcome = outcomeVisible ? _scanOutcomeKind : null,
            message = outcomeVisible ? _scanOutcomeMessage : null
        };
    }

    private void PublishScanState() => _webBridge?.SendEvent("scanChanged", ScanDto());

    private void PublishLibraryChanged() { _libraryQueries.InvalidateFolders(); _webBridge?.SendEvent("libraryChanged", new { }); }

    private void PublishSettingsChanged() => _webBridge?.SendEvent("settingsChanged", WebSettings());
}
