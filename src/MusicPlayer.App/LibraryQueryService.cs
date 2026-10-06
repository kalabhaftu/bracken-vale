using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BrackenVale.Core;

namespace BrackenVale.App;

/// <summary>
/// Owns database-backed browsing queries and the projection/identity mapping used by the
/// WebView library UI. It has no dependency on WinUI controls or window lifetime.
/// </summary>
internal sealed class LibraryQueryService
{
    private static readonly HashSet<string> Views = new(StringComparer.Ordinal)
    {
        "Home", "Search", "Songs", "Albums", "Artists", "Genres", "Folders", "Favorites", "Most Played",
        "Recently Played", "Recently Added", "Playlists", "Playlist", "Album", "Artist", "Genre", "Folder",
        "With Lyrics", "Queue", "Audio", "Settings", "Duplicates", "Lyrics", "Now Playing"
    };

    private readonly LibraryStore _store;
    private readonly string _artworkDirectory;
    private readonly Dictionary<string, string> _trackPaths = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unavailableTrackPaths = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly object _availabilityGate = new();
    private readonly HashSet<string> _unavailableRootPaths = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly object _rootAvailabilityGate = new();
    private IReadOnlyList<string>? _folderCache;
    private ViewContext _context = new("Home", "", null, null, null);
    private TrackSort _sort = TrackSort.Title;
    private bool _descending;

    public LibraryQueryService(LibraryStore store, string appDataDirectory)
    {
        _store = store;
        _artworkDirectory = Path.GetFullPath(Path.Combine(appDataDirectory, "Artwork"));
    }

    public string CurrentView => _context.View;
    public string CurrentSearch => _context.Search;
    public object? CurrentGroup
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_context.GroupColumn) || string.IsNullOrWhiteSpace(_context.GroupValue)) return null;
            return new { column = _context.GroupColumn, name = _context.GroupValue, id = _context.GroupValue };
        }
    }
    public object? CurrentPlaylist
    {
        get
        {
            if (_context.View != "Playlist" || string.IsNullOrWhiteSpace(_context.PlaylistId)) return null;
            var playlist = _store.GetPlaylistSummaries().FirstOrDefault(item => item.Id == _context.PlaylistId);
            return playlist is null ? null : PlaylistDto(playlist);
        }
    }

    public void RestoreContext(string? serialized)
    {
        if (string.IsNullOrWhiteSpace(serialized) || serialized.Length > 8192) return;
        try
        {
            using var document = JsonDocument.Parse(serialized);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return;
            SetContext(document.RootElement);
            if (_context.View == "Playlist" &&
                (string.IsNullOrWhiteSpace(_context.PlaylistId) || !_store.GetPlaylistSummaries().Any(item => item.Id == _context.PlaylistId)))
                SetContext(JsonDocument.Parse("{\"view\":\"Playlists\"}").RootElement);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            LocalAppLog.Shared.Warning("navigation", "The saved player view could not be restored; opening Home instead.", ex);
            _context = new("Home", "", null, null, null);
        }
    }

    public object HomeData(int requestedPageSize)
    {
        var pageSize = Math.Clamp(requestedPageSize, 1, 20);
        var stats = _store.GetLibraryStats();
        IReadOnlyList<Track> Page(string? filter, TrackSort sort) => _store.GetTracksPageWithCount(null, sort, true, filter,
            offset: 0, pageSize: pageSize, hideExactDuplicates: HideExactDuplicates).Tracks;
        var quick = Page("favorites", TrackSort.Added);
        if (quick.Count == 0) quick = Page("recent", TrackSort.LastPlayed);
        if (quick.Count == 0) quick = Page(null, TrackSort.Added);
        return new
        {
            totalTracks = stats.TotalTracks,
            totalBytes = stats.TotalBytes,
            quick = quick.Select(track => TrackDto(track)).ToArray(),
            recent = Page("recent", TrackSort.LastPlayed).Select(track => TrackDto(track)).ToArray(),
            mostPlayed = Page("most-played", TrackSort.PlayCount).Select(track => TrackDto(track)).ToArray(),
            recentlyAdded = Page(null, TrackSort.Added).Select(track => TrackDto(track)).ToArray()
        };
    }

    public object Search(JsonElement payload)
    {
        var query = String(payload, "query");
        var filter = String(payload, "filter");
        var offset = Math.Max(0, Int(payload, "offset"));
        var size = Math.Clamp(Int(payload, "pageSize", 40), 1, 100);
        return filter switch
        {
            "song" => SearchTrackPage(query, offset, size),
            "album" => SearchGroupPage("album", query, offset, size),
            "artist" => SearchGroupPage("artist", query, offset, size),
            "playlist" => SearchPlaylistPage(query, offset, size),
            _ => throw new ArgumentException("Choose a search result type.")
        };
    }

    public object TrackPage(JsonElement payload)
    {
        SetContext(payload);
        var filter = _context.View switch
        {
            "Favorites" => "favorites",
            "Most Played" => "most-played",
            "Recently Played" => "recent",
            "With Lyrics" => "with-lyrics",
            _ => null
        };
        var requestedSort = String(payload, "sort");
        var sortSpecified = payload.TryGetProperty("sort", out var sortValue) && sortValue.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(sortValue.GetString());
        var sort = sortSpecified ? ParseSort(requestedSort) : _context.View switch
        {
            "Most Played" => TrackSort.PlayCount,
            "Recently Played" => TrackSort.LastPlayed,
            "Recently Added" => TrackSort.Added,
            _ => TrackSort.Title
        };
        _sort = sort;
        var descendingSpecified = payload.TryGetProperty("descending", out var descendingValue) &&
                                  descendingValue.ValueKind is JsonValueKind.True or JsonValueKind.False;
        _descending = descendingSpecified
            ? Boolean(payload, "descending")
            : _context.View is "Most Played" or "Recently Played" or "Recently Added";
        var result = _store.GetTracksPageWithCount(_context.Search, sort, _descending, filter,
            _context.GroupColumn, _context.GroupValue, Math.Max(0, Int(payload, "offset")),
            Math.Clamp(Int(payload, "pageSize", 100), 1, 200), HideExactDuplicates);
        return new { tracks = result.Tracks.Select(track => TrackDto(track)).ToArray(), totalCount = result.TotalCount };
    }

    public object GroupPage(JsonElement payload)
    {
        var column = GroupColumnFromType(String(payload, "column"));
        var search = String(payload, "search");
        var offset = Math.Max(0, Int(payload, "offset"));
        var pageSize = Math.Clamp(Int(payload, "pageSize", 60), 1, 200);
        var groups = _store.GetGroupsPage(column, search, offset, pageSize).Select(group => GroupDto(group, column)).ToArray();
        return new { groups, totalCount = _store.CountGroups(column, search) };
    }

    public object FolderData(JsonElement payload, object scanState)
    {
        var roots = ReadJsonSetting("library-roots", Array.Empty<string>());
        var counts = _store.GetRootTrackCounts(roots);
        var rootDtos = roots.Select(path =>
        {
            var normalizedPath = NormalizeRootPath(path);
            bool scanUnavailable;
            lock (_rootAvailabilityGate) scanUnavailable = _unavailableRootPaths.Contains(normalizedPath);
            var available = Directory.Exists(path) && !scanUnavailable;
            return new
            {
                path,
                name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                trackCount = counts.GetValueOrDefault(path),
                available,
                status = available ? "Available" : "Unavailable"
            };
        }).ToArray();
        if (Boolean(payload, "rootsOnly"))
            return new { roots = rootDtos, folders = Array.Empty<object>(), folderCount = 0, folderOffset = 0, scan = scanState };

        var allFolders = _folderCache ??= _store.GetFolders();
        var offset = Math.Max(0, Int(payload, "offset"));
        var size = Math.Clamp(Int(payload, "pageSize", 50), 1, 100);
        var folders = allFolders.Skip(offset).Take(size).Select(path => new
        {
            path,
            name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            trackCount = _store.CountTracks(groupColumn: "folder", groupValue: path)
        }).ToArray();
        return new { roots = rootDtos, folders, folderCount = allFolders.Count, folderOffset = offset, scan = scanState };
    }

    public object ArtistAlbums(string artist, int requestedOffset, int requestedPageSize)
    {
        var offset = Math.Max(0, requestedOffset);
        var pageSize = Math.Clamp(requestedPageSize, 1, 60);
        return new { groups = _store.GetArtistAlbumsPage(artist, offset, pageSize).Select(group => GroupDto(group, "album")).ToArray() };
    }

    public object Playlists() => new { playlists = _store.GetPlaylistSummaries().Select(PlaylistDto).ToArray() };

    public void SetContext(JsonElement payload)
    {
        var requestedView = String(payload, "view");
        var view = _context.View;
        var search = _context.Search;
        var groupColumn = _context.GroupColumn;
        var groupValue = _context.GroupValue;
        var playlistId = _context.PlaylistId;

        if (!string.IsNullOrWhiteSpace(requestedView))
        {
            if (!Views.Contains(requestedView)) throw new ArgumentException("Unknown Music Player view.");
            view = requestedView;
        }
        if (payload.TryGetProperty("search", out var searchValue))
            search = searchValue.ValueKind == JsonValueKind.String ? searchValue.GetString() ?? "" : "";
        else if (!string.IsNullOrWhiteSpace(requestedView))
            search = "";

        if (payload.TryGetProperty("group", out var group))
        {
            if (group.ValueKind == JsonValueKind.Object)
            {
                groupColumn = String(group, "column");
                groupValue = String(group, "name");
            }
            else
            {
                groupColumn = null;
                groupValue = null;
            }
        }
        else if (payload.TryGetProperty("groupColumn", out var groupColumnValue))
        {
            groupColumn = groupColumnValue.ValueKind == JsonValueKind.String ? groupColumnValue.GetString() : null;
            groupValue = String(payload, "groupValue");
        }
        else if (!string.IsNullOrWhiteSpace(requestedView))
        {
            groupColumn = null;
            groupValue = null;
        }

        if (payload.TryGetProperty("playlistId", out var playlistValue))
            playlistId = playlistValue.ValueKind == JsonValueKind.String ? playlistValue.GetString() : null;
        else if (!string.IsNullOrWhiteSpace(requestedView) && requestedView != "Playlist")
            playlistId = null;

        _context = new(view, search, groupColumn, groupValue, playlistId);
        PersistContext();
    }

    public void SetPlaylistContext(string id)
    {
        _context = _context with { View = "Playlist", PlaylistId = id };
        PersistContext();
    }

    public string SetGroupContext(string type, string value)
    {
        var column = GroupColumnFromType(type);
        var view = column switch { "album" => "Album", "artist" => "Artist", _ => "Genre" };
        _context = new(view, "", column, value, null);
        PersistContext();
        return column;
    }

    private void PersistContext()
    {
        var serialized = JsonSerializer.Serialize(new
        {
            view = _context.View,
            search = _context.Search,
            groupColumn = _context.GroupColumn,
            groupValue = _context.GroupValue,
            playlistId = _context.PlaylistId
        });
        if (!string.Equals(_store.GetSetting("last-view-context"), serialized, StringComparison.Ordinal))
            _store.SetSetting("last-view-context", serialized);
    }

    public IReadOnlyList<string> CurrentTrackPaths()
    {
        var playlistId = _context.PlaylistId;
        if (_context.View == "Playlist" && !string.IsNullOrWhiteSpace(playlistId))
            return _store.GetPlaylistPaths(playlistId);
        var filter = _context.View switch
        {
            "Favorites" => "favorites",
            "Most Played" => "most-played",
            "Recently Played" => "recent",
            "With Lyrics" => "with-lyrics",
            _ => null
        };
        return _store.GetTrackPaths(_context.Search, _sort, _descending, filter,
            _context.GroupColumn, _context.GroupValue, hideExactDuplicates: HideExactDuplicates);
    }

    public IReadOnlyList<string> GroupTrackPaths(string column, string value) =>
        _store.GetTrackPaths(groupColumn: column, groupValue: value, hideExactDuplicates: HideExactDuplicates);

    public Track? ResolveTrack(string? id) =>
        !string.IsNullOrWhiteSpace(id) && _trackPaths.TryGetValue(id, out var path) ? _store.GetTrack(path) : null;

    public string? ResolveTrackPath(string? id) =>
        !string.IsNullOrWhiteSpace(id) && _trackPaths.TryGetValue(id, out var path) ? path : null;

    public bool SetTrackUnavailable(string path, bool unavailable)
    {
        var fullPath = Path.GetFullPath(path);
        lock (_availabilityGate)
            return unavailable ? _unavailableTrackPaths.Add(fullPath) : _unavailableTrackPaths.Remove(fullPath);
    }

    public bool IsTrackUnavailable(string path)
    {
        var fullPath = Path.GetFullPath(path);
        lock (_availabilityGate) return _unavailableTrackPaths.Contains(fullPath);
    }

    public void SetUnavailableRoots(IEnumerable<string> roots)
    {
        var normalizedRoots = roots.Select(NormalizeRootPath).ToArray();
        lock (_rootAvailabilityGate)
        {
            _unavailableRootPaths.Clear();
            foreach (var root in normalizedRoots) _unavailableRootPaths.Add(root);
        }
    }

    public string[] ClearUnavailableTracksThatExist()
    {
        string[] candidates;
        lock (_availabilityGate) candidates = _unavailableTrackPaths.ToArray();
        var available = candidates.Where(File.Exists).ToArray();
        lock (_availabilityGate)
            foreach (var path in available) _unavailableTrackPaths.Remove(path);
        return available;
    }

    public string TrackId(string path)
    {
        var id = OpaqueId(path);
        if (_trackPaths.Count >= 4096) _trackPaths.Clear();
        _trackPaths[id] = path;
        return id;
    }

    public string OpaqueId(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24];

    private static string NormalizeRootPath(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            return Path.TrimEndingDirectorySeparator(fullPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    public object TrackDto(Track track, bool includePath = false) => new
    {
        id = TrackId(track.Path), title = track.Title, artist = track.Artist, album = track.Album,
        albumArtist = track.AlbumArtist, genre = track.Genre, year = track.Year, trackNumber = track.TrackNumber,
        durationSeconds = track.Duration.TotalSeconds,
        addedDisplay = track.AddedUtc.ToLocalTime().ToString("d", CultureInfo.CurrentCulture),
        lastPlayedDisplay = track.LastPlayedUtc?.ToLocalTime().ToString("d", CultureInfo.CurrentCulture) ?? "",
        favorite = track.Favorite, rating = track.Rating, playCount = track.PlayCount,
        hasLyrics = track.HasLyrics,
        artworkUrl = ArtworkUrl(track.ArtworkPath), format = Path.GetExtension(track.Path).TrimStart('.').ToUpperInvariant(),
        unavailable = false, fileUnavailable = IsTrackUnavailable(track.Path),
        path = includePath ? track.Path : null
    };

    public object GroupDto(LibraryGroup group, string column) => new
    {
        id = group.Name, name = group.Name, title = group.Name, artist = group.Artist ?? "", albumArtist = group.Artist ?? "",
        trackCount = group.TrackCount, count = group.TrackCount, year = group.Year, column,
        artworkUrl = ArtworkUrl(group.ArtworkPath), meta = column == "album" ? group.Artist : $"{group.TrackCount} tracks"
    };

    public object PlaylistDto(PlaylistSummary playlist) => new
    { id = playlist.Id, name = playlist.Name, title = playlist.Name, trackCount = playlist.TrackCount, count = playlist.TrackCount, meta = $"{playlist.TrackCount} tracks" };

    public string? ArtworkUrl(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var full = Path.GetFullPath(path);
            var root = _artworkDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return null;
            return $"https://artwork.musicplayer.local/{Uri.EscapeDataString(Path.GetFileName(full))}";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return null; }
    }

    public void InvalidateFolders() => _folderCache = null;

    private bool HideExactDuplicates => _store.GetSetting("hide-exact-duplicates") != "false";

    private object SearchTrackPage(string query, int offset, int size)
    {
        var page = _store.GetTracksPageWithCount(query, TrackSort.Title, false, hideExactDuplicates: HideExactDuplicates,
            offset: offset, pageSize: size);
        return new { items = page.Tracks.Select(track => TrackDto(track)).ToArray(), totalCount = page.TotalCount };
    }

    private object SearchGroupPage(string column, string query, int offset, int size) => new
    {
        items = _store.GetGroupsPage(column, query, offset, size).Select(group => GroupDto(group, column)).ToArray(),
        totalCount = _store.CountGroups(column, query)
    };

    private object SearchPlaylistPage(string query, int offset, int size)
    {
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var playlists = _store.GetPlaylistSummaries().Where(playlist => terms.All(term =>
            playlist.Name.Contains(term, StringComparison.OrdinalIgnoreCase))).ToArray();
        return new { items = playlists.Skip(offset).Take(size).Select(PlaylistDto).ToArray(), totalCount = playlists.Length };
    }

    private T[] ReadJsonSetting<T>(string key, T[] fallback)
    {
        try { return _store.GetSetting(key) is { } json ? JsonSerializer.Deserialize<T[]>(json) ?? fallback : fallback; }
        catch (JsonException ex)
        {
            LocalAppLog.Shared.Warning("settings", $"Saved setting '{key}' was invalid JSON.", ex);
            return fallback;
        }
    }

    private static string GroupColumnFromType(string value) => value.ToLowerInvariant() switch
    { "album" => "album", "artist" => "artist", "genre" => "genre", "folder" => "folder", _ => throw new ArgumentException("Unknown library group.") };

    private static TrackSort ParseSort(string value) => Enum.TryParse<TrackSort>(value, true, out var sort) ? sort : TrackSort.Title;

    private static string String(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    private static int Int(JsonElement element, string key, int fallback = 0) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) && value.TryGetInt32(out var result)
            ? result : fallback;

    private static bool Boolean(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;

    private sealed record ViewContext(string View, string Search, string? GroupColumn, string? GroupValue, string? PlaylistId);
}
