using BrackenVale.Core;

namespace BrackenVale.App;

/// <summary>Watches configured music folders for removals so stale songs do not wait for a click.</summary>
internal sealed class LibraryFileWatcher : IDisposable
{
    private readonly object _gate = new();
    private readonly HashSet<string> _pendingRemoved = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Timer _flushTimer;
    private readonly Timer _rescanTimer;
    private bool _disposed;

    public event Action<IReadOnlyList<string>>? PathsRemoved;
    public event Action? RescanRequested;

    public LibraryFileWatcher()
    {
        _flushTimer = new Timer(_ => FlushRemoved(), null, Timeout.Infinite, Timeout.Infinite);
        _rescanTimer = new Timer(_ => RescanRequested?.Invoke(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void SetRoots(IEnumerable<string> roots)
    {
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var watcher in _watchers) watcher.Dispose();
            _watchers.Clear();

            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            foreach (var root in roots.Select(Path.GetFullPath).Distinct(comparer))
            {
                if (!Directory.Exists(root)) continue;
                try
                {
                    var watcher = new FileSystemWatcher(root)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                        InternalBufferSize = 32 * 1024,
                        EnableRaisingEvents = false
                    };
                    watcher.Deleted += (_, args) => QueueRemoved(args.FullPath);
                    watcher.Renamed += (_, args) => QueueRemoved(args.OldFullPath);
                    watcher.Error += (_, args) =>
                    {
                        LocalAppLog.Shared.Warning("library-watch", $"A library folder change could not be observed under '{root}'.", args.GetException());
                        QueueRescan();
                    };
                    watcher.EnableRaisingEvents = true;
                    _watchers.Add(watcher);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    LocalAppLog.Shared.Warning("library-watch", $"Could not watch library folder '{root}'. Startup and manual scans still reconcile it.", ex);
                }
            }
        }
    }

    private void QueueRemoved(string path)
    {
        lock (_gate)
        {
            if (_disposed) return;
            try { _pendingRemoved.Add(Path.GetFullPath(path)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return; }
            _flushTimer.Change(450, Timeout.Infinite);
        }
    }

    private void QueueRescan()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _rescanTimer.Change(1200, Timeout.Infinite);
        }
    }

    private void FlushRemoved()
    {
        string[] paths;
        lock (_gate)
        {
            if (_disposed || _pendingRemoved.Count == 0) return;
            paths = _pendingRemoved.ToArray();
            _pendingRemoved.Clear();
        }
        PathsRemoved?.Invoke(paths);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _flushTimer.Dispose();
            _rescanTimer.Dispose();
            foreach (var watcher in _watchers) watcher.Dispose();
            _watchers.Clear();
            _pendingRemoved.Clear();
        }
    }
}
