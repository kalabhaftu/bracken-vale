using System.Text.Json;
using MusicPlayer.Core;

namespace MusicPlayer.App;

/// <summary>Validates and persists library-root and scan-exclusion preferences.</summary>
internal sealed class LibraryLocationService(LibraryStore store)
{
    public string[] GetLibraryRoots() => ReadStringArray("library-roots");

    public string[] GetScanExclusions() => ReadStringArray("ignored-directories");

    public string AddLibraryRoot(string rawPath)
    {
        var path = NormalizeRoot(rawPath) ?? throw new ArgumentException("That folder path is not valid.");
        var roots = ReadStringArray("library-roots").ToList();
        var comparer = PathComparer;
        var exists = roots.Any(root => NormalizeRoot(root) is { } saved && comparer.Equals(saved, path));
        if (!exists) roots.Add(path);
        store.SetSetting("library-roots", JsonSerializer.Serialize(roots));
        store.SetSetting("library-roots-configured", "true");
        return path;
    }

    public LibraryRootRemoval? PrepareLibraryRootRemoval(string rawPath)
    {
        var path = NormalizeRoot(rawPath) ?? throw new ArgumentException("That folder path is not valid.");
        var comparer = PathComparer;
        var roots = ReadStringArray("library-roots").Select(NormalizeRoot).Where(root => root is not null).Cast<string>().ToList();
        var remaining = roots.Where(root => !comparer.Equals(root, path)).ToArray();
        var removed = roots.Where(root => comparer.Equals(root, path)).ToArray();
        return removed.Length == 0 ? null : new LibraryRootRemoval(removed, remaining);
    }

    public void PersistLibraryRootRemoval(LibraryRootRemoval removal)
    {
        store.SetSetting("library-roots", JsonSerializer.Serialize(removal.RemainingRoots));
        store.SetSetting("library-roots-configured", "true");
    }

    public void AddScanExclusion(string path)
    {
        var paths = ReadStringArray("ignored-directories").ToList();
        if (!paths.Contains(path, PathComparer)) paths.Add(path);
        store.SetSetting("ignored-directories", JsonSerializer.Serialize(paths));
    }

    public void RemoveScanExclusion(string path)
    {
        var paths = ReadStringArray("ignored-directories").Where(item => !PathComparer.Equals(item, path)).ToArray();
        store.SetSetting("ignored-directories", JsonSerializer.Serialize(paths));
    }

    private string[] ReadStringArray(string key)
    {
        try { return store.GetSetting(key) is { } json ? JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>() : Array.Empty<string>(); }
        catch (JsonException ex)
        {
            LocalAppLog.Shared.Warning("settings", $"Saved setting '{key}' was invalid JSON.", ex);
            return Array.Empty<string>();
        }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string? NormalizeRoot(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            LocalAppLog.Shared.Warning("library-roots", $"Ignored invalid saved library folder '{path}'.", ex);
            return null;
        }
    }
}

internal sealed record LibraryRootRemoval(string[] RemovedRoots, string[] RemainingRoots);
