using BrackenVale.Core;

namespace BrackenVale.App;

/// <summary>Owns the active library scan and its pause, resume, and cancellation control.</summary>
internal sealed class LibraryScanCoordinator(LibraryIndexer indexer)
{
    private readonly object _gate = new();
    private ScanControl? _activeScan;
    private bool _paused;

    public bool IsRunning
    {
        get { lock (_gate) return _activeScan is not null; }
    }

    public bool IsPaused
    {
        get { lock (_gate) return _activeScan is not null && _paused; }
    }

    public Task<IndexResult?> StartAsync(
        IEnumerable<string> roots,
        IEnumerable<string> ignoredDirectories,
        IProgress<ScanProgress>? progress = null)
    {
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var scanRoots = roots.Select(Path.GetFullPath).Distinct(pathComparer).ToArray();
        if (scanRoots.Length == 0) return Task.FromResult<IndexResult?>(null);

        ScanControl control;
        lock (_gate)
        {
            if (_activeScan is not null) return Task.FromResult<IndexResult?>(null);
            control = new ScanControl();
            _activeScan = control;
            _paused = false;
        }

        return RunAsync(scanRoots, ignoredDirectories, control, progress);
    }

    public bool TogglePause()
    {
        lock (_gate)
        {
            if (_activeScan is not { } control) return false;
            if (_paused) control.Resume();
            else control.Pause();
            _paused = !_paused;
            return true;
        }
    }

    public void Cancel()
    {
        lock (_gate) _activeScan?.Cancel();
    }

    private async Task<IndexResult?> RunAsync(
        string[] roots,
        IEnumerable<string> ignoredDirectories,
        ScanControl control,
        IProgress<ScanProgress>? progress)
    {
        try
        {
            return await Task.Run(() => indexer.ScanAsync(roots, ignoredDirectories, control, progress)).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeScan, control))
                {
                    _activeScan = null;
                    _paused = false;
                }
                control.Dispose();
            }
        }
    }
}
