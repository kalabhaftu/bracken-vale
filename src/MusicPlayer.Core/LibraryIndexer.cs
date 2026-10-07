using System.Security;

namespace MusicPlayer.Core;

public sealed record IndexResult(int Indexed, int Skipped, int Removed = 0, IReadOnlyList<string>? UnavailableRoots = null, IReadOnlyList<string>? IncompletePaths = null, int FilesFound = 0);

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
        var filesFound = 0;
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
                filesFound++;
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
        // Explicit rebuilds also repair already-indexed files that no longer sit under
        // today's configured roots. Normal scans cheaply revisit only broken artwork.
        var repaired = await RefreshIndexedFilesAsync(forceRefresh, scanRoots, control).ConfigureAwait(false);
        indexed += repaired;
        var removed = scan.Complete();
        if (removed > 0) store.InvalidateExactFingerprintSnapshot();
        store.PruneUnreferencedArtwork(artworkCache);
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return new(indexed, skipped, removed, unavailableRoots.Distinct(comparer).ToArray(), incompletePaths.Distinct(comparer).ToArray(), filesFound);
    }

    private async Task<int> RefreshIndexedFilesAsync(bool forceRefresh, IReadOnlyList<string> scanRoots, ScanControl control)
    {
        const int pageSize = 400;
        var changed = 0;
        var batch = new List<Track>(32);
        async Task RefreshOneAsync(Track indexedTrack)
        {
            control.Token.ThrowIfCancellationRequested();
            await control.WaitIfPausedAsync().ConfigureAwait(false);
            if (!File.Exists(indexedTrack.Path)) return;
            try
            {
                batch.Add(await Task.Run(() => TrackReader.Read(indexedTrack.Path, artworkCache), control.Token).ConfigureAwait(false));
                if (batch.Count >= 32)
                {
                    store.UpsertTracks(batch);
                    changed += batch.Count;
                    batch.Clear();
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or TagLib.CorruptFileException or TagLib.UnsupportedFormatException or NotSupportedException or ArgumentException)
            {
                _log.Warning("indexer", "Could not refresh metadata for an existing indexed track.", ex);
            }
        }

        if (forceRefresh)
        {
            // Traverse by immutable path so metadata/title updates do not shift later
            // pages. Keep each query and its metadata objects bounded for large libraries.
            for (var offset = 0; ; offset += pageSize)
            {
                control.Token.ThrowIfCancellationRequested();
                var page = store.GetTracksPage(sort: TrackSort.Path, offset: offset, pageSize: pageSize);
                foreach (var track in page)
                {
                    if (scanRoots.Any(root => IsWithinRoot(root, track.Path))) continue;
                    await RefreshOneAsync(track).ConfigureAwait(false);
                }
                if (page.Count < pageSize) break;
            }
        }
        else
        {
            // Normal scans only query bounded pages and reopen files whose cached cover
            // disappeared. Avoid one database connection per song in large libraries.
            for (var offset = 0; ; offset += pageSize)
            {
                control.Token.ThrowIfCancellationRequested();
                // Artwork repair updates metadata too, which can change titles. Page
                // by immutable path so those updates cannot shift unseen rows between
                // offsets and leave stale artwork behind.
                var page = store.GetTracksPage(sort: TrackSort.Path, offset: offset, pageSize: pageSize);
                foreach (var track in page)
                {
                    if (string.IsNullOrWhiteSpace(track.ArtworkPath) || File.Exists(track.ArtworkPath)) continue;
                    await RefreshOneAsync(track).ConfigureAwait(false);
                }
                if (page.Count < pageSize) break;
            }
        }
        if (batch.Count > 0)
        {
            store.UpsertTracks(batch);
            changed += batch.Count;
        }
        return changed;
    }

    private static bool IsWithinRoot(string root, string path)
    {
        try
        {
            var relative = Path.GetRelativePath(root, path);
            return !Path.IsPathRooted(relative) && relative != ".." &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static bool MayStillExist(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { return true; }
    }
}
