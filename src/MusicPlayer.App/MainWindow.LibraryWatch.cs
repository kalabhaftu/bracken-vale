using MusicPlayer.Core;
using Microsoft.UI.Xaml.Controls;

namespace MusicPlayer.App;

public sealed partial class MainWindow
{
    private readonly SemaphoreSlim _fileWatcherReconcileGate = new(1, 1);

    private async Task HandleLibraryPathsRemovedAsync(IReadOnlyList<string> paths)
    {
        await _fileWatcherReconcileGate.WaitAsync();
        try
        {
            var unavailable = await Task.Run(() => _trackAvailability.ReconcileRemovedPaths(paths));
            if (unavailable.Length == 0 || _windowClosed) return;

            DispatcherQueue.TryEnqueue(() =>
            {
                if (_windowClosed) return;
                foreach (var path in unavailable)
                    _webBridge?.SendEvent("trackAvailabilityChanged", new { id = _libraryQueries.TrackId(path), fileUnavailable = true });

                PublishLibraryChanged();
                PublishQueueState(force: true);
                var currentWasRemoved = _playback.CurrentTrack is { } current &&
                    unavailable.Any(path => SameTrack(path, current.Path));
                if (currentWasRemoved)
                {
                    PublishPlaybackState();
                    _ = ShowNoticeAsync("The current song was removed from its folder. Its saved playlist and queue references were kept.", InfoBarSeverity.Warning);
                }
            });
        }
        catch (Exception ex)
        {
            LocalAppLog.Shared.Warning("library-watch", "Could not reconcile removed library files. A rescan will retry.", ex);
            RequestLibraryWatcherRescan();
        }
        finally { _fileWatcherReconcileGate.Release(); }
    }

    private void RequestLibraryWatcherRescan()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_windowClosed) return;
            if (_libraryScan.IsRunning)
            {
                _libraryWatcherRescanPending = true;
                return;
            }
            var roots = _libraryLocations.GetLibraryRoots();
            if (roots.Length > 0) StartScan(roots);
        });
    }

    private void RunPendingLibraryWatcherRescan()
    {
        if (!_libraryWatcherRescanPending || _windowClosed) return;
        _libraryWatcherRescanPending = false;
        RequestLibraryWatcherRescan();
    }
}
