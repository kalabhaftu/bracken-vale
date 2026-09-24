namespace BrackenVale.Core;

public sealed record IndexResult(int Indexed, int Skipped);

public sealed class LibraryIndexer(LibraryStore store, string artworkCache)
{
    public async Task<IndexResult> ScanAsync(
        IEnumerable<string> roots,
        IEnumerable<string> ignoredDirectories,
        ScanControl control,
        IProgress<ScanProgress>? progress = null)
    {
        var pending = new List<Track>(64);
        var known = store.GetIndexedFileStates().ToDictionary(pair => pair.Key, pair => pair.Value, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var indexed = 0;
        var skipped = 0;
        var scanner = new LibraryScanner();
        await scanner.ScanAsync(roots, ignoredDirectories, control, (path, token) =>
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(path);
                var fullPath = Path.GetFullPath(path);
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
            }
            return ValueTask.CompletedTask;
        }, progress).ConfigureAwait(false);
        if (pending.Count > 0)
        {
            store.UpsertTracks(pending);
            indexed += pending.Count;
        }
        return new(indexed, skipped);
    }
}
