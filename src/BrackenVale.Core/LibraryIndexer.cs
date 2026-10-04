using System.Security;

namespace BrackenVale.Core;

public sealed record IndexResult(int Indexed, int Skipped, int Removed = 0);

public sealed class LibraryIndexer(LibraryStore store, string artworkCache, LocalAppLog? log = null)
{
    private readonly LocalAppLog _log = log ?? LocalAppLog.Shared;

    public async Task<IndexResult> ScanAsync(
        IEnumerable<string> roots,
        IEnumerable<string> ignoredDirectories,
        ScanControl control,
        IProgress<ScanProgress>? progress = null)
    {
        var pending = new List<Track>(64);
        var scanRoots = roots.Select(Path.GetFullPath).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
        using var scan = store.BeginScan(scanRoots);
        var ignored = ignoredDirectories.Select(Path.GetFullPath).ToArray();
        var indexed = 0;
        var skipped = 0;
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
                scan.MarkSeen(fullPath);
                try
                {
                    var info = new FileInfo(path);
                    if (scan.IsUnchanged(fullPath, info.Length, info.LastWriteTimeUtc))
                        return ValueTask.CompletedTask;
                    pending.Add(TrackReader.Read(path, artworkCache));
                    if (pending.Count >= 64) FlushPending();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or TagLib.CorruptFileException or TagLib.UnsupportedFormatException or NotSupportedException or ArgumentException)
                {
                    skipped++;
                    _log.Warning("indexer", $"Skipped audio file '{fullPath}'.", ex);
                }
                return ValueTask.CompletedTask;
            }, progress, rootCompleted: scan.MarkRootCompleted, pathExcluded: scan.MarkExcludedPath).ConfigureAwait(false);
        }
        catch
        {
            FlushPending();
            throw;
        }
        FlushPending();
        var removed = scan.Complete();
        store.PruneUnreferencedArtwork(artworkCache);
        return new(indexed, skipped, removed);
    }
}
