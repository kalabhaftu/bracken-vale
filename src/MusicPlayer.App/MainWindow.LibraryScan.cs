using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using BrackenVale.Core;
using Microsoft.UI.Composition;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Windowing;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace BrackenVale.App;

public sealed partial class MainWindow : Window
{
    private void StartStartupScan()
    {
        var roots = _libraryLocations.GetLibraryRoots().ToList();
        if (_store.GetSetting("library-roots-configured") != "true")
        {
            roots = DefaultMusicRoots().ToList();
            _store.SetSetting("library-roots", JsonSerializer.Serialize(roots));
            _store.SetSetting("library-roots-configured", "true");
        }
        else if (roots.Count > 0 && roots.All(IsVolumeRoot))
        {
            // Earlier builds silently scanned every fixed drive. Narrow that implicit default
            // to the user's Music folder while preserving any explicitly chosen subfolders.
            roots = DefaultMusicRoots().ToList();
            _store.SetSetting("library-roots", JsonSerializer.Serialize(roots));
        }
        _libraryFileWatcher.SetRoots(roots);
        if (roots.Count > 0) StartScan(roots);
    }

    private static IEnumerable<string> DefaultMusicRoots()
    {
        var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        return !string.IsNullOrWhiteSpace(music) && Directory.Exists(music) ? [Path.GetFullPath(music)] : [];
    }

    private static bool IsVolumeRoot(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            return !string.IsNullOrEmpty(root) && string.Equals(
                Path.TrimEndingDirectorySeparator(fullPath), Path.TrimEndingDirectorySeparator(root),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private async void StartScan(IEnumerable<string> roots, bool forceRefresh = false)
    {
        if (_libraryScan.IsRunning) return;
        var scanRoots = roots.Select(Path.GetFullPath).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
        if (scanRoots.Length == 0) return;
        _scanStartedUtc = DateTimeOffset.UtcNow;
        ClearScanOutcome();
        _scanCurrentPath = "Preparing scan…";
        _scanFilesFound = 0;
        _scanDirectoriesVisited = 0;
        try
        {
            var ignored = _libraryLocations.GetScanExclusions();
            var nextLibraryRefresh = 256;
            var progress = new Progress<ScanProgress>(value =>
            {
                if (_windowClosed) return;
                _scanFilesFound = value.FilesFound;
                _scanDirectoriesVisited = value.DirectoriesVisited;
                if (!string.IsNullOrWhiteSpace(value.CurrentPath)) _scanCurrentPath = value.CurrentPath;
                if (DateTimeOffset.UtcNow - _lastWebScanUpdateUtc >= TimeSpan.FromMilliseconds(350))
                { _lastWebScanUpdateUtc = DateTimeOffset.UtcNow; PublishScanState(); }
                if (value.FilesFound >= nextLibraryRefresh)
                {
                    PublishLibraryChanged();
                    nextLibraryRefresh = value.FilesFound + 256;
                }
            });
            var scanTask = _libraryScan.StartAsync(scanRoots, ignored, progress, forceRefresh);
            if (!_libraryScan.IsRunning) return;
            PublishScanState();
            var result = await scanTask;
            if (!_windowClosed)
            {
                if (result is not null)
                {
                    _scanCurrentPath = "Scan complete";
                    var unavailableRoots = result.UnavailableRoots ?? Array.Empty<string>();
                    _libraryQueries.SetUnavailableRoots(unavailableRoots);
                    if (unavailableRoots.Count > 0)
                    {
                        _trackAvailability.MarkUnavailableRoots(unavailableRoots);
                        var unavailableIds = new HashSet<string>(StringComparer.Ordinal);
                        if (_playback.CurrentTrack is { } current && _libraryQueries.IsTrackUnavailable(current.Path))
                            unavailableIds.Add(_libraryQueries.TrackId(current.Path));
                        foreach (var path in _queue)
                            if (_libraryQueries.IsTrackUnavailable(path)) unavailableIds.Add(_libraryQueries.TrackId(path));
                        foreach (var id in unavailableIds)
                            _webBridge?.SendEvent("trackAvailabilityChanged", new { id, fileUnavailable = true });
                    }
                    foreach (var path in _trackAvailability.ClearUnavailableTracksThatExist())
                        _webBridge?.SendEvent("trackAvailabilityChanged", new { id = _libraryQueries.TrackId(path), fileUnavailable = false });

                    // Reconcile every indexed path after a successful scan. This also
                    // catches files deleted before the watcher started or outside the
                    // currently selected roots, while the availability service retains
                    // references whose containing drive or folder cannot be reached.
                    var reconciliation = await Task.Run(_trackAvailability.ReconcileIndexedTracks);
                    foreach (var path in reconciliation.RemovedPaths)
                        _webBridge?.SendEvent("trackAvailabilityChanged", new { id = _libraryQueries.TrackId(path), fileUnavailable = true });
                    foreach (var path in _trackAvailability.ClearUnavailableTracksThatExist())
                        _webBridge?.SendEvent("trackAvailabilityChanged", new { id = _libraryQueries.TrackId(path), fileUnavailable = false });

                    var heading = forceRefresh ? "Index rebuilt" : "Scan complete";
                    var removedCount = result.Removed + reconciliation.RemovedPaths.Count;
                    var message = $"{heading} · {result.Indexed:N0} tracks updated · {removedCount:N0} missing tracks removed · {result.Skipped:N0} files skipped";
                    if (reconciliation.UnavailableCount > 0)
                        message += $" · {reconciliation.UnavailableCount:N0} tracks retained because their locations are unavailable";
                    if (unavailableRoots.Count > 0)
                        message += $" · unavailable folders (tracks kept): {string.Join("; ", unavailableRoots)}";
                    if (result.IncompletePaths is { Count: > 0 } incompletePaths)
                        message += $" · {incompletePaths.Count:N0} folders could not be checked; their indexed tracks were kept";
                    if (reconciliation.RemovedPaths.Count > 0)
                        LocalAppLog.Shared.Info("library-availability", $"Reconciled {reconciliation.RemovedPaths.Count:N0} confirmed missing indexed track(s); retained {reconciliation.UnavailableCount:N0} unreachable track(s).");
                    SetScanOutcome("complete", message);
                }
                PublishLibraryChanged(); PublishScanState();
            }
        }
        catch (OperationCanceledException)
        {
            LocalAppLog.Shared.Info("scanner", "Library scan cancelled; completed tracks were retained.");
            SetScanOutcome("cancelled", "Scan cancelled · tracks already indexed were kept");
            if (!_windowClosed) { _scanCurrentPath = "Scan cancelled"; PublishScanState(); }
        }
        catch (Exception ex)
        {
            LocalAppLog.Shared.Error("scanner", "Library scan failed.", ex);
            SetScanOutcome("error", $"Scan failed · {ex.Message}");
            if (!_windowClosed) { _scanCurrentPath = "Scan stopped"; PublishScanState(); _ = ShowNoticeAsync($"The library scan failed: {ex.Message}", InfoBarSeverity.Error); }
        }
        finally
        {
            if (!_windowClosed)
            {
                PublishLibraryChanged(); PublishScanState();
                RunPendingLibraryWatcherRescan();
            }
        }
    }

    private void ClearScanOutcome()
    {
        _scanOutcomeKind = null;
        _scanOutcomeMessage = null;
        _scanOutcomeUtc = null;
    }

    private void SetScanOutcome(string kind, string message)
    {
        _scanOutcomeKind = kind;
        _scanOutcomeMessage = message;
        _scanOutcomeUtc = DateTimeOffset.UtcNow;
    }
}
