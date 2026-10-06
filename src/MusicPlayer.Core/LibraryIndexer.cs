using System.Security;

namespace BrackenVale.Core;

public sealed record IndexResult(int Indexed, int Skipped, int Removed = 0, IReadOnlyList<string>? UnavailableRoots = null, IReadOnlyList<string>? IncompletePaths = null);

public sealed class LibraryIndexer(LibraryStore store, string artworkCache, LocalAppLog? log = null)
{
    private readonly LocalAppLog _log = log ?? LocalAppLog.Shared;

    public async Task<IndexResult> ScanAsync(
        IEnumerable<string> roots,
        IEnumerable<string> ignoredDirectories,
        ScanControl control,
        IProgress<ScanProgress>? progress = null,
        bool forceRefresh = false)
    {
        var pending = new List<Track>(64);
        var scanRoots = roots.Select(Path.GetFullPath).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
        // A scan is an explicit request to reconcile the current filesystem. Revalidate
        // cached content hashes as paths are encountered after a drive was unavailable.
        store.InvalidateExactFingerprintSnapshot();
        using var scan = store.BeginScan(scanRoots);
        var ignored = ignoredDirectories.Select(Path.GetFullPath).ToArray();
        var indexed = 0;
        var skipped = 0;
        var unavailableRoots = new List<string>();
        var incompletePaths = new List<string>();
        void FlushPending()
        {
            if (pending.Count == 0) return;
            store.UpsertTracks(pending);
            indexed += pending.Count;
            pending.Clear();
        }
        var scanner = new LibraryScanner(_log);
        try
        {
            await scanner.ScanAsync(scanRoots, ignored, control, (path, token) =>
            {
                token.ThrowIfCancellationRequested();
                var fullPath = Path.GetFullPath(path);
                try
                {
                    var info = new FileInfo(path);
                    if (!info.Exists)
                    {
                        if (MayStillExist(fullPath)) scan.MarkSeen(fullPath);
                        skipped++;
                        return ValueTask.CompletedTask;
                    }
                    if (!forceRefresh && scan.IsUnchanged(fullPath, info.Length, info.LastWriteTimeUtc, artworkCache))
                    {
                        scan.MarkSeen(fullPath);
                        return ValueTask.CompletedTask;
                    }
                    var track = TrackReader.Read(path, artworkCache);
                    scan.MarkSeen(fullPath);
                    pending.Add(track);
                    if (pending.Count >= 64) FlushPending();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or TagLib.CorruptFileException or TagLib.UnsupportedFormatException or NotSupportedException or ArgumentException)
                {
                    // Keep an unreadable but still-present audio file in the index; if it
                    // disappeared during this scan, leave it unseen so Complete() prunes it.
                    if (MayStillExist(fullPath)) scan.MarkSeen(fullPath);
                    skipped++;
                    _log.Warning("indexer", $"Skipped audio file '{fullPath}'.", ex);
                }
                return ValueTask.CompletedTask;
            }, progress, rootCompleted: scan.MarkRootCompleted,
                rootUnavailable: root => unavailableRoots.Add(root),
                pathExcludedWithReason: scan.MarkExcludedPath,
                pathIncomplete: path => { scan.MarkPathIncomplete(path); incompletePaths.Add(path); }).ConfigureAwait(false);
        }
        catch
        {
            FlushPending();
            throw;
        }
        FlushPending();
        var removed = scan.Complete();
        if (removed > 0) store.InvalidateExactFingerprintSnapshot();
        store.PruneUnreferencedArtwork(artworkCache);
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return new(indexed, skipped, removed, unavailableRoots.Distinct(comparer).ToArray(), incompletePaths.Distinct(comparer).ToArray());
    }

    private static bool MayStillExist(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { return true; }
    }
}
