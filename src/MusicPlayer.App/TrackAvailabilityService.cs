using System.Security;
using BrackenVale.Core;

namespace BrackenVale.App;

/// <summary>Distinguishes removed files from temporarily unreachable library locations.</summary>
internal sealed class TrackAvailabilityService(
    LibraryStore store,
    LibraryQueryService queries,
    LibraryLocationService locations)
{
    public AvailabilityReconciliation ReconcileIndexedTracks(IEnumerable<string>? unavailableRoots = null, IEnumerable<string>? incompletePaths = null)
    {
        const int pageSize = 800;
        var removed = new List<string>();
        var unavailable = 0;
        var recovered = 0;
        var protectedPrefixes = (unavailableRoots ?? Array.Empty<string>())
            .Concat(incompletePaths ?? Array.Empty<string>())
            .Select(TryNormalizePrefix).Where(prefix => prefix is not null).Cast<string>().ToArray();
        var offset = 0;
        while (true)
        {
            var paths = store.GetTrackPaths(offset: offset, pageSize: pageSize);
            if (paths.Count == 0) break;
            var removedFromPage = new List<string>();
            foreach (var path in paths)
            {
                if (File.Exists(path))
                {
                    if (queries.SetTrackUnavailable(path, false)) recovered++;
                    continue;
                }

                queries.SetTrackUnavailable(path, true);
                if (!protectedPrefixes.Any(prefix => IsAncestorDirectory(prefix, path)) && IsConfirmedMissingFile(path)) removedFromPage.Add(path);
                else unavailable++;
            }

            if (removedFromPage.Count > 0)
            {
                store.RemoveTracks(removedFromPage);
                removed.AddRange(removedFromPage);
                // Deletions collapse the ordered result set; revisit this page's offset
                // so the shifted tracks are not skipped.
            }
            else
            {
                offset += paths.Count;
                if (paths.Count < pageSize) break;
            }
        }
        return new(removed, unavailable, recovered);
    }

    public bool MarkUnavailable(string path)
    {
        queries.SetTrackUnavailable(path, true);
        var confirmedMissing = IsConfirmedMissingFile(path);
        if (confirmedMissing) store.RemoveTracks([path]);
        return confirmedMissing;
    }

    public bool MarkAvailable(string path) => queries.SetTrackUnavailable(path, false);

    public int MarkUnavailableRoots(IEnumerable<string> roots)
    {
        var marked = 0;
        foreach (var root in roots)
        {
            const int pageSize = 800;
            for (var offset = 0; ; offset += pageSize)
            {
                var paths = store.GetTrackPaths(groupColumn: "folder", groupValue: root, offset: offset, pageSize: pageSize);
                foreach (var path in paths)
                    if (queries.SetTrackUnavailable(path, true)) marked++;
                if (paths.Count < pageSize) break;
            }
        }
        return marked;
    }

    /// <summary>Reconciles a filesystem deletion event and returns indexed/queued paths confirmed missing.</summary>
    public string[] ReconcileRemovedPaths(IEnumerable<string> deletedPaths)
    {
        var candidates = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var rawPath in deletedPaths)
        {
            string path;
            try { path = Path.GetFullPath(rawPath); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }

            // A watcher can report an enclosing directory when a folder is deleted. Resolve
            // its indexed descendants in bounded pages instead of dropping the whole root.
            if (store.GetTrack(path) is not null || LibraryScanner.IsSupportedAudioFile(path))
            {
                candidates.Add(path);
                continue;
            }

            const int pageSize = 800;
            for (var offset = 0; ; offset += pageSize)
            {
                var paths = store.GetTrackPaths(groupColumn: "folder", groupValue: path, offset: offset, pageSize: pageSize);
                foreach (var trackPath in paths) candidates.Add(trackPath);
                if (paths.Count < pageSize) break;
            }
        }

        var confirmedMissing = new List<string>();
        foreach (var path in candidates)
        {
            // Rename/delete notifications can race a replacement at the same path.
            if (File.Exists(path) || !IsConfirmedMissingFile(path)) continue;
            queries.SetTrackUnavailable(path, true);
            if (store.GetTrack(path) is not null) confirmedMissing.Add(path);
        }

        if (confirmedMissing.Count > 0) store.RemoveTracks(confirmedMissing);
        return candidates.Where(path => queries.IsTrackUnavailable(path)).ToArray();
    }

    public string[] ClearUnavailableTracksThatExist() => queries.ClearUnavailableTracksThatExist();

    private bool IsConfirmedMissingFile(string path)
    {
        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }

        var parent = Path.GetDirectoryName(fullPath);
        var parentAvailable = !string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent);
        var accessibleConfiguredRootContainsPath = !parentAvailable && locations.GetLibraryRoots()
            .Any(root => IsAncestorDirectory(root, fullPath) && Directory.Exists(root));
        var storageVolumeAvailable = IsStorageVolumeAvailable(fullPath);
        if (!parentAvailable && !accessibleConfiguredRootContainsPath && !storageVolumeAvailable) return false;

        try { _ = File.GetAttributes(fullPath); return false; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return parentAvailable || accessibleConfiguredRootContainsPath || storageVolumeAvailable; }
        catch (UnauthorizedAccessException) { return false; }
        catch (SecurityException) { return false; }
        catch (IOException) { return false; }
    }

    private static bool IsStorageVolumeAvailable(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            return !string.IsNullOrWhiteSpace(root) && new DriveInfo(root).IsReady;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static string? TryNormalizePrefix(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static bool IsAncestorDirectory(string root, string path)
    {
        try
        {
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var relative = Path.GetRelativePath(fullRoot, path);
            return !Path.IsPathRooted(relative) && relative != "." &&
                relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}

internal sealed record AvailabilityReconciliation(IReadOnlyList<string> RemovedPaths, int UnavailableCount, int RecoveredCount);
