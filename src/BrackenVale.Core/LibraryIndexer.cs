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
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var known = store.GetIndexedFileStates().ToDictionary(pair => pair.Key, pair => pair.Value, comparer);
        var seen = new HashSet<string>(comparer);
        var visitedDirectories = new HashSet<string>(comparer);
        var ignored = ignoredDirectories.Select(Path.GetFullPath).ToArray();
        var indexed = 0;
        var skipped = 0;
        var scanner = new LibraryScanner(_log);
        await scanner.ScanAsync(roots, ignored, control, (path, token) =>
        {
            token.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(path);
            seen.Add(fullPath);
            try
            {
                var info = new FileInfo(path);
                if (known.TryGetValue(fullPath, out var state) && state.Length == info.Length && state.ModifiedUtc == info.LastWriteTimeUtc)
                    return ValueTask.CompletedTask;
                pending.Add(TrackReader.Read(path, artworkCache));
                known[fullPath] = new(info.Length, info.LastWriteTimeUtc);
                if (pending.Count >= 64)
                {
                    store.UpsertTracks(pending);
                    indexed += pending.Count;
                    pending.Clear();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TagLib.CorruptFileException or TagLib.UnsupportedFormatException or NotSupportedException or ArgumentException)
            {
                skipped++;
                _log.Warning("indexer", $"Skipped audio file '{fullPath}'.", ex);
            }
            return ValueTask.CompletedTask;
        }, progress, directory => visitedDirectories.Add(Path.GetFullPath(directory))).ConfigureAwait(false);
        if (pending.Count > 0)
        {
            store.UpsertTracks(pending);
            indexed += pending.Count;
        }
        var removed = known.Keys.Where(path => !seen.Contains(path) &&
            (visitedDirectories.Contains(Path.GetDirectoryName(path)!) || ignored.Any(folder => IsSameOrBelow(path, folder)))).ToArray();
        store.RemoveTracks(removed);
        return new(indexed, skipped, removed.Length);
    }

    private static bool IsSameOrBelow(string path, string folder)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(folder), Path.GetFullPath(path));
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }
}
