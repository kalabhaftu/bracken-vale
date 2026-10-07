using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MusicPlayer.Core;

public enum TrackSort { Title, Artist, Album, Genre, Year, Added, Duration, PlayCount, LastPlayed, Path, Rating, TrackNumber }
public sealed record IndexedFileState(long Length, DateTime ModifiedUtc);

public sealed partial class LibraryStore
{
    private const int SchemaVersion = 8;
    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly LocalAppLog _log;
    private sealed record TrackQuery(string Where, string OrderBy, string[] SearchTerms, string? FtsPhrase, object GroupValue, object FolderPrefix);

    public LibraryStore(string databasePath, LocalAppLog? log = null)
    {
        _databasePath = Path.GetFullPath(databasePath);
        _log = log ?? LocalAppLog.Shared;
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _databasePath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared, DefaultTimeout = 10, ForeignKeys = true }.ToString();
        Migrate();
    }

    public static LibraryStore InAppData()
    {
        return new(Path.Combine(AppDataPaths.Root, "library.db"));
    }

    public void UpsertTrack(Track track)
    {
        lock (_fingerprintGate)
        {
            using var connection = Open();
            Upsert(connection, null, track);
            _fingerprintSnapshotCurrent = false;
        }
    }

    public void UpsertTracks(IEnumerable<Track> tracks)
    {
        lock (_fingerprintGate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            foreach (var track in tracks) Upsert(connection, transaction, track);
            transaction.Commit();
            _fingerprintSnapshotCurrent = false;
        }
    }

    public void RemoveTracks(IEnumerable<string> paths)
    {
        lock (_fingerprintGate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM tracks WHERE path=$path";
            var path = command.Parameters.Add("$path", SqliteType.Text);
            foreach (var item in paths)
            {
                path.Value = Path.GetFullPath(item);
                command.ExecuteNonQuery();
            }
            transaction.Commit();
            _fingerprintSnapshotCurrent = false;
        }
    }

    /// <summary>Deletes index rows under a removed root unless another configured root still covers them.</summary>
    /// <remarks>Media files and playlist path entries are never deleted.</remarks>
    public int RemoveTracksUnderUnselectedRoot(string removedRoot, IEnumerable<string> remainingRoots) =>
        RemoveTracksUnderUnselectedRoots([removedRoot], remainingRoots);

    /// <summary>Atomically removes index rows under any removed root while preserving paths covered by remaining roots.</summary>
    /// <remarks>Media files and playlist path entries are never deleted.</remarks>
    public int RemoveTracksUnderUnselectedRoots(IEnumerable<string> removedRoots, IEnumerable<string> remainingRoots)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var roots = removedRoots.Select(NormalizeRoot).Distinct(comparer).ToArray();
        if (roots.Length == 0) return 0;
        var remaining = remainingRoots.Select(NormalizeRoot).Distinct(comparer).ToArray();
        lock (_fingerprintGate)
        {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = "CREATE TEMP TABLE IF NOT EXISTS remaining_roots(path TEXT PRIMARY KEY, prefix TEXT NOT NULL) WITHOUT ROWID; DELETE FROM remaining_roots; CREATE TEMP TABLE IF NOT EXISTS removed_roots(path TEXT PRIMARY KEY, prefix TEXT NOT NULL) WITHOUT ROWID; DELETE FROM removed_roots;";
            create.ExecuteNonQuery();
        }
        foreach (var (table, paths) in new[] { ("remaining_roots", remaining), ("removed_roots", roots) })
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = $"INSERT OR IGNORE INTO {table}(path,prefix) VALUES($path,$prefix)";
            var path = insert.Parameters.Add("$path", SqliteType.Text);
            var prefix = insert.Parameters.Add("$prefix", SqliteType.Text);
            foreach (var item in paths)
            {
                path.Value = item;
                prefix.Value = RootPrefix(item);
                insert.ExecuteNonQuery();
            }
        }
        using var remove = connection.CreateCommand();
        remove.Transaction = transaction;
        var collation = OperatingSystem.IsWindows() ? " COLLATE NOCASE" : string.Empty;
        remove.CommandText = $"""
            DELETE FROM tracks
            WHERE EXISTS (
                SELECT 1 FROM removed_roots x
                WHERE tracks.path{collation}=x.path OR substr(tracks.path,1,length(x.prefix)){collation}=x.prefix)
              AND NOT EXISTS (
                SELECT 1 FROM remaining_roots r
                WHERE (tracks.path{collation}=r.path OR substr(tracks.path,1,length(r.prefix)){collation}=r.prefix))
            """;
        var removed = remove.ExecuteNonQuery();
        transaction.Commit();
        if (removed > 0) _fingerprintSnapshotCurrent = false;
        return removed;
        }
    }

    /// <summary>Deletes only unreferenced artwork files from the flat artwork cache.</summary>
    public int PruneUnreferencedArtwork(string artworkCache)
    {
        var directory = Path.GetFullPath(artworkCache);
        if (!Directory.Exists(directory)) return 0;
        var extensions = new HashSet<string>([".png", ".jpg", ".jpeg", ".webp", ".img"], StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = OperatingSystem.IsWindows()
            ? "SELECT 1 FROM tracks WHERE artwork_path COLLATE NOCASE=$path LIMIT 1"
            : "SELECT 1 FROM tracks WHERE artwork_path=$path LIMIT 1";
        command.Parameters.Add("$path", SqliteType.Text);
        command.Prepare();
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                if (!extensions.Contains(Path.GetExtension(file))) continue;
                command.Parameters["$path"].Value = Path.GetFullPath(file);
                if (command.ExecuteScalar() is not null) continue;
                try { System.IO.File.Delete(file); removed++; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
                { _log.Warning("artwork-cache", $"Could not remove unreferenced artwork '{file}'.", ex); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        { _log.Warning("artwork-cache", $"Could not enumerate artwork cache '{directory}'.", ex); }
        catch (SqliteException ex)
        { _log.Warning("artwork-cache", "Could not check artwork references while pruning the cache.", ex); }
        return removed;
    }

    private static void Upsert(SqliteConnection connection, SqliteTransaction? transaction, Track track)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO tracks(path,title,artist,album,album_artist,genre,year,track_number,duration_ms,file_size,modified_utc,added_utc,favorite,rating,play_count,last_played_utc,artwork_path,has_lyrics)
            VALUES($path,$title,$artist,$album,$albumArtist,$genre,$year,$track,$duration,$size,$modified,$added,$favorite,$rating,$plays,$played,$artwork,$hasLyrics)
            ON CONFLICT(path) DO UPDATE SET title=excluded.title,artist=excluded.artist,album=excluded.album,album_artist=excluded.album_artist,
              genre=excluded.genre,year=excluded.year,track_number=excluded.track_number,duration_ms=excluded.duration_ms,file_size=excluded.file_size,
              modified_utc=excluded.modified_utc,artwork_path=excluded.artwork_path,has_lyrics=excluded.has_lyrics
            """;
        BindTrack(command, track);
        command.ExecuteNonQuery();
        RememberTrackPath(connection, transaction, track.Path);
    }

    public static string TrackHandleId(string path) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path))))[..24];

    public void RememberTrackPath(string path)
    {
        using var connection = Open();
        RememberTrackPath(connection, null, path);
    }

    private static void RememberTrackPath(SqliteConnection connection, SqliteTransaction? transaction, string path)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO track_handles(id,path) VALUES($id,$path)";
        Add(command, "$id", TrackHandleId(path));
        Add(command, "$path", Path.GetFullPath(path));
        command.ExecuteNonQuery();
    }

    public string? ResolveTrackHandle(string id)
    {
        if (id.Length != 24 || !id.All(Uri.IsHexDigit)) return null;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path FROM track_handles WHERE id=$id";
        Add(command, "$id", id);
        return command.ExecuteScalar() as string;
    }

    public Track? GetTrack(string path)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM tracks WHERE path=$path";
        Add(command, "$path", Path.GetFullPath(path));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadTrack(reader) : null;
    }

    public IReadOnlyDictionary<string, IndexedFileState> GetIndexedFileStates()
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT path,file_size,modified_utc FROM tracks";
        using var reader = command.ExecuteReader();
        var states = new Dictionary<string, IndexedFileState>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        while (reader.Read()) states[reader.GetString(0)] = new(reader.GetInt64(1), ParseStamp(reader.GetString(2)));
        return states;
    }

    public LibraryScanSession BeginScan(IEnumerable<string> roots)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var normalized = roots.Select(NormalizeRoot).Distinct(comparer).ToArray();
        return new LibraryScanSession(_connectionString, normalized);
    }

    public IReadOnlyList<Track> GetTracks(string? search = null, TrackSort sort = TrackSort.Title, bool descending = false, string? filter = null, string? groupColumn = null, string? groupValue = null, bool hideExactDuplicates = false, CancellationToken cancellationToken = default)
    {
        return QueryTracks(search, sort, descending, filter, groupColumn, groupValue, null, 0, false, hideExactDuplicates, cancellationToken).Tracks;
    }

    /// <summary>Returns a bounded stable page of matching tracks.</summary>
    public IReadOnlyList<Track> GetTracksPage(
        string? search = null, TrackSort sort = TrackSort.Title, bool descending = false,
        string? filter = null, string? groupColumn = null, string? groupValue = null,
        int offset = 0, int pageSize = 200, bool hideExactDuplicates = false, CancellationToken cancellationToken = default)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (pageSize is < 1 or > 800) throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be between 1 and 800 tracks.");
        return QueryTracks(search, sort, descending, filter, groupColumn, groupValue, offset, pageSize, false, hideExactDuplicates, cancellationToken).Tracks;
    }

    public int CountTracks(string? search = null, TrackSort sort = TrackSort.Title, bool descending = false,
        string? filter = null, string? groupColumn = null, string? groupValue = null, bool hideExactDuplicates = false, CancellationToken cancellationToken = default) =>
        QueryTracks(search, sort, descending, filter, groupColumn, groupValue, null, 0, true, hideExactDuplicates, cancellationToken).Count;

    /// <summary>Returns path-only queue entries in the same stable order as the matching track query.</summary>
    public IReadOnlyList<string> GetTrackPaths(string? search = null, TrackSort sort = TrackSort.Title, bool descending = false,
        string? filter = null, string? groupColumn = null, string? groupValue = null, int offset = 0, int? pageSize = null,
        bool hideExactDuplicates = false, CancellationToken cancellationToken = default)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (pageSize is < 1 or > 800) throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be between 1 and 800 paths.");
        var query = BuildTrackQuery(search, sort, descending, filter, groupColumn, groupValue);
        if (hideExactDuplicates) EnsureExactFingerprintsCurrent(cancellationToken);
        using var connection = Open();
        using var command = connection.CreateCommand();
        var prefix = hideExactDuplicates ? BuildDeduplicatedCte(query) : string.Empty;
        command.CommandText = $"{prefix} SELECT path FROM {(hideExactDuplicates ? "ranked_tracks" : "tracks")} WHERE {(hideExactDuplicates ? "duplicate_rank=1" : query.Where)} ORDER BY {query.OrderBy}" +
            (pageSize.HasValue ? " LIMIT $limit OFFSET $offset" : offset > 0 ? " LIMIT -1 OFFSET $offset" : string.Empty);
        BindTrackQuery(command, query);
        if (pageSize.HasValue) Add(command, "$limit", pageSize.Value);
        if (pageSize.HasValue || offset > 0) Add(command, "$offset", offset);
        using var reader = command.ExecuteReader();
        var paths = new List<string>(pageSize ?? 0);
        while (reader.Read()) { cancellationToken.ThrowIfCancellationRequested(); paths.Add(reader.GetString(0)); }
        return paths;
    }

    private (IReadOnlyList<Track> Tracks, int Count) QueryTracks(string? search, TrackSort sort, bool descending, string? filter,
        string? groupColumn, string? groupValue, int? offset, int pageSize, bool countOnly, bool hideExactDuplicates = false,
        CancellationToken cancellationToken = default, bool fingerprintsAlreadyCurrent = false)
    {
        var query = BuildTrackQuery(search, sort, descending, filter, groupColumn, groupValue);
        if (hideExactDuplicates && !fingerprintsAlreadyCurrent) EnsureExactFingerprintsCurrent(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        using var command = connection.CreateCommand();
        using var cancellation = cancellationToken.Register(command.Cancel);
        if (countOnly && hideExactDuplicates)
        {
            // Counts need one row per identity, but do not need the representative ranking
            // used by result pages. Counting the identity key avoids sorting/projecting every
            // matching track through ROW_NUMBER() on each filtered-library count request.
            command.CommandText = $"{BuildFingerprintCte(query)} SELECT COUNT(DISTINCT CASE " +
                "WHEN exact_fingerprint IS NULL THEN 'path:'||path " +
                "ELSE 'sha256:'||fingerprint_size||':'||exact_fingerprint END) FROM filtered_tracks";
        }
        else
        {
            var prefix = hideExactDuplicates ? BuildDeduplicatedCte(query) : string.Empty;
            var from = hideExactDuplicates ? "ranked_tracks" : "tracks";
            var where = hideExactDuplicates ? "duplicate_rank=1" : query.Where;
            command.CommandText = prefix + (countOnly
                ? $" SELECT COUNT(*) FROM {from} WHERE {where}"
                : $" SELECT * FROM {from} WHERE {where} ORDER BY {query.OrderBy}" +
                  (offset.HasValue ? " LIMIT $limit OFFSET $offset" : string.Empty));
        }
        BindTrackQuery(command, query);
        if (offset.HasValue) { Add(command, "$limit", pageSize); Add(command, "$offset", offset.Value); }
        if (countOnly)
        {
            var count = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            cancellationToken.ThrowIfCancellationRequested();
            return (Array.Empty<Track>(), count);
        }
        using var reader = command.ExecuteReader();
        var result = new List<Track>(offset.HasValue ? pageSize : 0);
        while (reader.Read()) { cancellationToken.ThrowIfCancellationRequested(); result.Add(ReadTrack(reader)); }
        return (result, 0);
    }

    private static TrackQuery BuildTrackQuery(string? search, TrackSort sort, bool descending, string? filter,
        string? groupColumn, string? groupValue)
    {
        var orderBy = sort switch
        {
            TrackSort.Artist => "artist", TrackSort.Album => "album", TrackSort.Genre => "genre", TrackSort.Year => "year",
            TrackSort.Added => "added_utc", TrackSort.Duration => "duration_ms", TrackSort.PlayCount => "play_count",
            TrackSort.LastPlayed => "last_played_utc", TrackSort.Path => "path", TrackSort.Rating => "rating",
            TrackSort.TrackNumber => "track_number", _ => "title"
        };
        var predicate = filter switch
        {
            "favorites" => "favorite=1", "most-played" => "play_count>0", "recent" => "last_played_utc IS NOT NULL", "with-lyrics" => "has_lyrics=1",
            _ => "1=1"
        };
        var groupPredicate = groupColumn switch
        {
            "album" => "($group IS NULL OR album=$group)", "artist" => "($group IS NULL OR artist=$group)",
            "genre" => "($group IS NULL OR genre=$group)", "folder" => "($folderPrefix IS NULL OR tracks.path LIKE $folderPrefix ESCAPE '\\')",
            null => "1=1", _ => throw new ArgumentOutOfRangeException(nameof(groupColumn))
        };
        var searchTerms = SplitSearchTerms(search);
        var perTermPredicates = searchTerms.Select((_, index) =>
            $"(title LIKE $pattern{index} ESCAPE '\\' OR artist LIKE $pattern{index} ESCAPE '\\' OR album LIKE $pattern{index} ESCAPE '\\' OR album_artist LIKE $pattern{index} ESCAPE '\\' OR genre LIKE $pattern{index} ESCAPE '\\' OR tracks.path LIKE $pattern{index} ESCAPE '\\')");
        var searchPredicate = searchTerms.Length == 0 ? "1=1" : $"({string.Join(" AND ", perTermPredicates)})";
        var ftsTerms = searchTerms.Where(term => term.Length >= 3 && term.All(char.IsLetterOrDigit)).ToArray();
        var ftsPhrase = ftsTerms.Length == 0 ? null : string.Join(" AND ", ftsTerms.Select(QuoteFtsTerm));
        if (ftsPhrase is not null)
            searchPredicate = $"tracks.rowid IN (SELECT rowid FROM tracks_fts WHERE tracks_fts MATCH $ftsPhrase) AND {searchPredicate}";
        var where = $"{predicate} AND {groupPredicate} AND {searchPredicate}";
        var fullOrderBy = $"{orderBy} {(descending ? "DESC" : "ASC")}, title COLLATE NOCASE, path COLLATE NOCASE, path";
        object groupParameter = string.IsNullOrWhiteSpace(groupValue) || groupColumn == "folder" ? DBNull.Value : groupValue;
        string? folderPrefix = null;
        if (groupColumn == "folder" && !string.IsNullOrWhiteSpace(groupValue))
        {
            var folder = Path.GetFullPath(groupValue);
            var prefix = Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar;
            folderPrefix = EscapeLike(prefix) + "%";
        }
        return new(where, fullOrderBy, searchTerms, ftsPhrase, groupParameter,
            folderPrefix is null ? DBNull.Value : folderPrefix);
    }

    private static void BindTrackQuery(SqliteCommand command, TrackQuery query)
    {
        for (var index = 0; index < query.SearchTerms.Length; index++)
            Add(command, $"$pattern{index}", $"%{EscapeLike(query.SearchTerms[index])}%");
        if (query.FtsPhrase is not null) Add(command, "$ftsPhrase", query.FtsPhrase);
        Add(command, "$group", query.GroupValue);
        Add(command, "$folderPrefix", query.FolderPrefix);
    }

    public IReadOnlyList<string> GetGroups(string column)
    {
        var safeColumn = column switch { "album" => "album", "artist" => "artist", "genre" => "genre", _ => throw new ArgumentOutOfRangeException(nameof(column)) };
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT DISTINCT {safeColumn} FROM tracks WHERE {safeColumn} <> '' ORDER BY {safeColumn} COLLATE NOCASE";
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values;
    }

    /// <summary>Returns a bounded page of album, artist, or genre cards with representative artwork.</summary>
    public IReadOnlyList<LibraryGroup> GetGroupsPage(string column, string? search = null, int offset = 0, int pageSize = 100,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (pageSize is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(pageSize), "Group page size must be between 1 and 200.");
        var safeColumn = column switch { "album" => "album", "artist" => "artist", "genre" => "genre", _ => throw new ArgumentOutOfRangeException(nameof(column)) };
        var terms = SplitSearchTerms(search);
        var match = BuildColumnSearchPredicate(safeColumn, terms, "$groupSearch");
        using var connection = Open(); using var command = connection.CreateCommand();
        using var cancellation = cancellationToken.Register(command.Cancel);
        command.CommandText = $"SELECT {safeColumn},COUNT(*),MIN(artwork_path),MIN(artist),MIN(year) FROM tracks WHERE {safeColumn}<>'' AND {match} GROUP BY {safeColumn} ORDER BY {safeColumn} COLLATE NOCASE LIMIT $limit OFFSET $offset";
        BindColumnSearch(command, terms, "$groupSearch");
        Add(command, "$limit", pageSize); Add(command, "$offset", offset);
        using var reader = command.ExecuteReader();
        var groups = new List<LibraryGroup>(pageSize);
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            groups.Add(new(reader.GetString(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? 0 : checked((uint)reader.GetInt64(4))));
        }
        return groups;
    }

    public int CountGroups(string column, string? search = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var safeColumn = column switch { "album" => "album", "artist" => "artist", "genre" => "genre", _ => throw new ArgumentOutOfRangeException(nameof(column)) };
        var terms = SplitSearchTerms(search);
        var match = BuildColumnSearchPredicate(safeColumn, terms, "$groupSearch");
        using var connection = Open(); using var command = connection.CreateCommand();
        using var cancellation = cancellationToken.Register(command.Cancel);
        command.CommandText = $"SELECT COUNT(DISTINCT {safeColumn}) FROM tracks WHERE {safeColumn}<>'' AND {match}";
        BindColumnSearch(command, terms, "$groupSearch");
        var count = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        cancellationToken.ThrowIfCancellationRequested();
        return count;
    }

    public IReadOnlyList<LibraryGroup> GetArtistAlbumsPage(string artist, int offset = 0, int pageSize = 50)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (pageSize is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(pageSize));
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT album,COUNT(*),MIN(artwork_path),MIN(album_artist),MIN(year) FROM tracks WHERE album<>'' AND artist=$artist GROUP BY album ORDER BY album COLLATE NOCASE LIMIT $limit OFFSET $offset";
        Add(command, "$artist", artist); Add(command, "$limit", pageSize); Add(command, "$offset", offset);
        using var reader = command.ExecuteReader(); var groups = new List<LibraryGroup>(pageSize);
        while (reader.Read()) groups.Add(new(reader.GetString(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? 0 : checked((uint)reader.GetInt64(4))));
        return groups;
    }

    public IReadOnlyList<string> GetFolders()
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT path FROM tracks";
        using var reader = command.ExecuteReader();
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var folders = new HashSet<string>(comparer);
        while (reader.Read())
            if (Path.GetDirectoryName(reader.GetString(0)) is { } folder) folders.Add(folder);
        return folders.OrderBy(folder => folder, comparer).ToArray();
    }

    public LibraryStats GetLibraryStats()
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*),COALESCE(SUM(file_size),0) FROM tracks";
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetInt32(0), reader.GetInt64(1)) : new(0, 0);
    }

    public IReadOnlyDictionary<string, int> GetRootTrackCounts(IEnumerable<string> roots)
    {
        using var connection = Open();
        var counts = new Dictionary<string, int>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var rawRoot in roots)
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rawRoot));
            var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            using var command = connection.CreateCommand();
            command.CommandText = OperatingSystem.IsWindows()
                ? "SELECT COUNT(*) FROM tracks WHERE path LIKE $pattern ESCAPE '\\' COLLATE NOCASE"
                : "SELECT COUNT(*) FROM tracks WHERE path LIKE $pattern ESCAPE '\\'";
            Add(command, "$pattern", EscapeLike(prefix) + "%");
            counts[rawRoot] = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        return counts;
    }

    public void SetFavorite(string path, bool favorite) => UpdateTrack(path, "favorite", favorite ? 1 : 0);
    public void SetRating(string path, int rating)
    {
        if (rating is < 0 or > 5) throw new ArgumentOutOfRangeException(nameof(rating), "Rating must be between zero and five stars.");
        UpdateTrack(path, "rating", rating);
    }

    public void RecordPlayed(string path, DateTime playedUtc)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE tracks SET play_count=play_count+1,last_played_utc=$played WHERE path=$path";
        Add(command, "$played", Stamp(playedUtc)); Add(command, "$path", Path.GetFullPath(path));
        command.ExecuteNonQuery();
    }

    public void SetSetting(string key, string value)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        Add(command, "$key", key); Add(command, "$value", value); command.ExecuteNonQuery();
    }

    public string? GetSetting(string key)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key=$key";
        Add(command, "$key", key);
        return command.ExecuteScalar() as string;
    }

    public IReadOnlyDictionary<string, string> GetSettings(string keyPrefix)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT key,value FROM settings WHERE key LIKE $prefix ESCAPE '\\' ORDER BY key COLLATE NOCASE";
        Add(command, "$prefix", keyPrefix.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%");
        using var reader = command.ExecuteReader(); var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) values[reader.GetString(0)] = reader.GetString(1);
        return values;
    }

    public void SaveSession(PlaybackSession session)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO playback_session(id,payload) VALUES(1,$payload) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload";
        Add(command, "$payload", JsonSerializer.Serialize(session)); command.ExecuteNonQuery();
    }

    public PlaybackSession? LoadSession()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM playback_session WHERE id=1";
        if (command.ExecuteScalar() is not string payload) return null;
        try { return JsonSerializer.Deserialize<PlaybackSession>(payload); }
        catch (JsonException ex)
        {
            _log.Warning("session", "Saved playback session was invalid and could not be restored.", ex);
            return null;
        }
    }

    public void RecordTagBackup(TagBackup backup)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO tag_backups(original_path,backup_path,created_utc) VALUES($path,$backup,$created)";
            Add(command, "$path", Path.GetFullPath(backup.OriginalPath)); Add(command, "$backup", Path.GetFullPath(backup.BackupPath)); Add(command, "$created", Stamp(backup.CreatedUtc));
            command.ExecuteNonQuery();
        }
        using (var prune = connection.CreateCommand())
        {
            prune.Transaction = transaction;
            prune.CommandText = "DELETE FROM tag_backups WHERE original_path=$path AND id NOT IN (SELECT id FROM tag_backups WHERE original_path=$path ORDER BY created_utc DESC,id DESC LIMIT 5)";
            Add(prune, "$path", Path.GetFullPath(backup.OriginalPath));
            prune.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public IReadOnlyList<TagBackup> GetTagBackups(string path)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT original_path,backup_path,created_utc FROM tag_backups WHERE original_path=$path ORDER BY created_utc DESC,id DESC LIMIT 5";
        Add(command, "$path", Path.GetFullPath(path));
        using var reader = command.ExecuteReader();
        var backups = new List<TagBackup>(5);
        while (reader.Read()) backups.Add(new(reader.GetString(0), reader.GetString(1), ParseStamp(reader.GetString(2))));
        return backups;
    }

    public TagBackup? GetLatestTagBackup(string path)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT original_path,backup_path,created_utc FROM tag_backups WHERE original_path=$path ORDER BY created_utc DESC,id DESC LIMIT 1";
        Add(command, "$path", Path.GetFullPath(path));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), reader.GetString(1), ParseStamp(reader.GetString(2))) : null;
    }

    public Playlist CreatePlaylist(string name, IEnumerable<string>? paths = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A playlist needs a name.", nameof(name));
        var id = Guid.NewGuid().ToString("N");
        var created = DateTime.UtcNow;
        var fullPaths = (paths ?? []).Select(Path.GetFullPath).ToArray();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO playlists(id,name,created_utc) VALUES($id,$name,$created)";
            Add(command, "$id", id); Add(command, "$name", name.Trim()); Add(command, "$created", Stamp(created)); command.ExecuteNonQuery();
        }
        AddPaths(connection, transaction, id, fullPaths);
        transaction.Commit();
        return new(id, name.Trim(), fullPaths, created);
    }

    public void AddToPlaylist(string playlistId, IEnumerable<string> paths)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var exists = connection.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = "SELECT 1 FROM playlists WHERE id=$id";
            Add(exists, "$id", playlistId);
            if (exists.ExecuteScalar() is null) throw new KeyNotFoundException("Playlist not found.");
        }
        AddPaths(connection, transaction, playlistId, paths);
        transaction.Commit();
    }

    public IReadOnlyList<Playlist> GetPlaylists()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,name,created_utc FROM playlists ORDER BY name COLLATE NOCASE";
        var entries = new List<(string Id, string Name, DateTime Created)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read()) entries.Add((reader.GetString(0), reader.GetString(1), ParseStamp(reader.GetString(2))));
        }
        var result = new List<Playlist>(entries.Count);
        foreach (var entry in entries)
        {
            var paths = new List<string>();
            using var child = connection.CreateCommand();
            child.CommandText = "SELECT track_path FROM playlist_tracks WHERE playlist_id=$id ORDER BY position";
            Add(child, "$id", entry.Id);
            using var pathReader = child.ExecuteReader();
            while (pathReader.Read()) paths.Add(pathReader.GetString(0));
            result.Add(new(entry.Id, entry.Name, paths, entry.Created));
        }
        return result;
    }

    public IReadOnlyList<PlaylistSummary> GetPlaylistSummaries()
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT p.id,p.name,p.created_utc,COUNT(pt.position) FROM playlists p LEFT JOIN playlist_tracks pt ON pt.playlist_id=p.id GROUP BY p.id,p.name,p.created_utc ORDER BY p.name COLLATE NOCASE";
        using var reader = command.ExecuteReader();
        var summaries = new List<PlaylistSummary>();
        while (reader.Read()) summaries.Add(new(reader.GetString(0), reader.GetString(1), ParseStamp(reader.GetString(2)), reader.GetInt32(3)));
        return summaries;
    }

    public IReadOnlyList<PlaylistEntry> GetPlaylistEntriesPage(string playlistId, string? search = null, int offset = 0, int pageSize = 200)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (pageSize is < 1 or > 800) throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be between 1 and 800 playlist entries.");
        var (match, pattern, ftsPhrase) = BuildPlaylistSearch(search);
        var collation = OperatingSystem.IsWindows() ? " COLLATE NOCASE" : string.Empty;
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = $"SELECT p.position,p.track_path,t.path,t.title,t.artist,t.album,t.album_artist,t.genre,t.year,t.track_number,t.duration_ms,t.file_size,t.modified_utc,t.added_utc,t.favorite,t.rating,t.play_count,t.last_played_utc,t.artwork_path,t.has_lyrics FROM playlist_tracks p LEFT JOIN tracks t ON t.path{collation}=p.track_path WHERE p.playlist_id=$id AND {match} ORDER BY p.position LIMIT $limit OFFSET $offset";
        Add(command, "$id", playlistId); Add(command, "$pattern", pattern is null ? DBNull.Value : pattern);
        if (ftsPhrase is not null) Add(command, "$ftsPhrase", ftsPhrase);
        Add(command, "$limit", pageSize); Add(command, "$offset", offset);
        using var reader = command.ExecuteReader();
        var entries = new List<PlaylistEntry>(pageSize);
        while (reader.Read())
        {
            var track = reader.IsDBNull(2) ? null : ReadTrack(reader);
            entries.Add(new(reader.GetInt32(0), reader.GetString(1), track));
        }
        return entries;
    }

    public IReadOnlyList<string> GetPlaylistPaths(string playlistId, string? search = null, int offset = 0, int? pageSize = null)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (pageSize is < 1 or > 800) throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be between 1 and 800 playlist paths.");
        var (match, pattern, ftsPhrase) = BuildPlaylistSearch(search);
        var collation = OperatingSystem.IsWindows() ? " COLLATE NOCASE" : string.Empty;
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = $"SELECT p.track_path FROM playlist_tracks p LEFT JOIN tracks t ON t.path{collation}=p.track_path WHERE p.playlist_id=$id AND {match} ORDER BY p.position" +
            (pageSize.HasValue ? " LIMIT $limit OFFSET $offset" : offset > 0 ? " LIMIT -1 OFFSET $offset" : string.Empty);
        Add(command, "$id", playlistId); Add(command, "$pattern", pattern is null ? DBNull.Value : pattern);
        if (ftsPhrase is not null) Add(command, "$ftsPhrase", ftsPhrase);
        if (pageSize.HasValue) Add(command, "$limit", pageSize.Value);
        if (pageSize.HasValue || offset > 0) Add(command, "$offset", offset);
        using var reader = command.ExecuteReader();
        var paths = new List<string>(pageSize ?? 0);
        while (reader.Read()) paths.Add(reader.GetString(0));
        return paths;
    }

    public int CountPlaylistEntries(string playlistId, string? search = null)
    {
        var (match, pattern, ftsPhrase) = BuildPlaylistSearch(search);
        var collation = OperatingSystem.IsWindows() ? " COLLATE NOCASE" : string.Empty;
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM playlist_tracks p LEFT JOIN tracks t ON t.path{collation}=p.track_path WHERE p.playlist_id=$id AND {match}";
        Add(command, "$id", playlistId); Add(command, "$pattern", pattern is null ? DBNull.Value : pattern);
        if (ftsPhrase is not null) Add(command, "$ftsPhrase", ftsPhrase);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public int CountPlaylistPathOccurrencesBefore(string playlistId, string path, int position)
    {
        if (position < 0) throw new ArgumentOutOfRangeException(nameof(position));
        using var connection = Open(); using var command = connection.CreateCommand();
        var collation = OperatingSystem.IsWindows() ? " COLLATE NOCASE" : string.Empty;
        command.CommandText = $"SELECT COUNT(*) FROM playlist_tracks WHERE playlist_id=$id AND track_path{collation}=$path AND position<$position";
        Add(command, "$id", playlistId); Add(command, "$path", Path.GetFullPath(path)); Add(command, "$position", position);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static (string Match, string? Pattern, string? FtsPhrase) BuildPlaylistSearch(string? search)
    {
        var normalized = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        if (normalized is null) return ("1=1", null, null);
        var escaped = normalized.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
        var pattern = $"%{escaped}%";
        var metadata = "t.title LIKE $pattern ESCAPE '\\' OR t.artist LIKE $pattern ESCAPE '\\' OR t.album LIKE $pattern ESCAPE '\\' OR t.album_artist LIKE $pattern ESCAPE '\\' OR t.genre LIKE $pattern ESCAPE '\\' OR t.path LIKE $pattern ESCAPE '\\'";
        var match = normalized.Length >= 3
            ? $"(p.track_path LIKE $pattern ESCAPE '\\' OR (t.rowid IN (SELECT rowid FROM tracks_fts WHERE tracks_fts MATCH $ftsPhrase) AND ({metadata})))"
            : $"(p.track_path LIKE $pattern ESCAPE '\\' OR {metadata})";
        var phrase = normalized.Length >= 3 ? "\"" + normalized.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : null;
        return (match, pattern, phrase);
    }

    public void RenamePlaylist(string id, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A playlist needs a name.", nameof(name));
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE playlists SET name=$name WHERE id=$id"; Add(command, "$id", id); Add(command, "$name", name.Trim());
        if (command.ExecuteNonQuery() == 0) throw new KeyNotFoundException("Playlist not found.");
    }

    public void DeletePlaylist(string id)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        using (var items = connection.CreateCommand()) { items.Transaction = transaction; items.CommandText = "DELETE FROM playlist_tracks WHERE playlist_id=$id"; Add(items, "$id", id); items.ExecuteNonQuery(); }
        using (var playlist = connection.CreateCommand()) { playlist.Transaction = transaction; playlist.CommandText = "DELETE FROM playlists WHERE id=$id"; Add(playlist, "$id", id); if (playlist.ExecuteNonQuery() == 0) throw new KeyNotFoundException("Playlist not found."); }
        transaction.Commit();
    }

    public void RemoveFromPlaylist(string id, int position)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        using (var remove = connection.CreateCommand())
        {
            remove.Transaction = transaction; remove.CommandText = "DELETE FROM playlist_tracks WHERE playlist_id=$id AND position=$position";
            Add(remove, "$id", id); Add(remove, "$position", position);
            if (remove.ExecuteNonQuery() == 0) { transaction.Rollback(); return; }
        }
        using (var reorder = connection.CreateCommand()) { reorder.Transaction = transaction; reorder.CommandText = "UPDATE playlist_tracks SET position=position-1 WHERE playlist_id=$id AND position>$position"; Add(reorder, "$id", id); Add(reorder, "$position", position); reorder.ExecuteNonQuery(); }
        transaction.Commit();
    }

    public void ImportM3u8(string playlistFile, string? name = null) => CreatePlaylist(name ?? Path.GetFileNameWithoutExtension(playlistFile), Playlists.ReadM3u8(playlistFile));
    public void ExportM3u8(string playlistId, string destination)
    {
        using (var connection = Open())
        using (var exists = connection.CreateCommand())
        {
            exists.CommandText = "SELECT 1 FROM playlists WHERE id=$id";
            Add(exists, "$id", playlistId);
            if (exists.ExecuteScalar() is null) throw new KeyNotFoundException("Playlist not found.");
        }
        Playlists.WriteM3u8(destination, ReadPlaylistPaths(playlistId));
    }

    private IEnumerable<string> ReadPlaylistPaths(string playlistId)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT track_path FROM playlist_tracks WHERE playlist_id=$id ORDER BY position";
        Add(command, "$id", playlistId);
        using var reader = command.ExecuteReader();
        while (reader.Read()) yield return reader.GetString(0);
    }

    private void UpdateTrack(string path, string column, int value)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE tracks SET {column}=$value WHERE path=$path";
        Add(command, "$value", value); Add(command, "$path", Path.GetFullPath(path)); command.ExecuteNonQuery();
    }

    private static void AddPaths(SqliteConnection connection, SqliteTransaction transaction, string id, IEnumerable<string> paths)
    {
        using var count = connection.CreateCommand();
        count.Transaction = transaction; count.CommandText = "SELECT COALESCE(MAX(position),-1)+1 FROM playlist_tracks WHERE playlist_id=$id"; Add(count, "$id", id);
        var index = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction; insert.CommandText = "INSERT INTO playlist_tracks(playlist_id,position,track_path) VALUES($id,$position,$path)";
        var pId = insert.Parameters.Add("$id", SqliteType.Text); var pPosition = insert.Parameters.Add("$position", SqliteType.Integer); var pPath = insert.Parameters.Add("$path", SqliteType.Text);
        foreach (var path in paths)
        {
            pId.Value = id; pPosition.Value = index++; pPath.Value = Path.GetFullPath(path); insert.ExecuteNonQuery();
        }
    }

    private void Migrate()
    {
        var hadDatabaseFile = System.IO.File.Exists(_databasePath);
        using var connection = Open();
        using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(versionCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (version > SchemaVersion) throw new InvalidDataException($"Database version {version} is newer than this app supports.");
        if (version == 0)
        {
            using var transaction = connection.BeginTransaction();
            using var createV1 = connection.CreateCommand();
            createV1.Transaction = transaction;
            createV1.CommandText = """
                CREATE TABLE IF NOT EXISTS tracks(
                  path TEXT PRIMARY KEY, title TEXT NOT NULL, artist TEXT NOT NULL DEFAULT '', album TEXT NOT NULL DEFAULT '', album_artist TEXT NOT NULL DEFAULT '',
                  genre TEXT NOT NULL DEFAULT '', year INTEGER NOT NULL DEFAULT 0, track_number INTEGER NOT NULL DEFAULT 0, duration_ms INTEGER NOT NULL DEFAULT 0,
                  file_size INTEGER NOT NULL DEFAULT 0, modified_utc TEXT NOT NULL, added_utc TEXT NOT NULL, favorite INTEGER NOT NULL DEFAULT 0,
                  rating INTEGER NOT NULL DEFAULT 0 CHECK(rating BETWEEN 0 AND 5), play_count INTEGER NOT NULL DEFAULT 0,
                  last_played_utc TEXT NULL, artwork_path TEXT NULL);
                CREATE INDEX IF NOT EXISTS ix_tracks_title ON tracks(title COLLATE NOCASE);
                CREATE INDEX IF NOT EXISTS ix_tracks_artist ON tracks(artist COLLATE NOCASE);
                CREATE INDEX IF NOT EXISTS ix_tracks_album ON tracks(album COLLATE NOCASE);
                CREATE TABLE IF NOT EXISTS playlists(id TEXT PRIMARY KEY, name TEXT NOT NULL, created_utc TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS playlist_tracks(playlist_id TEXT NOT NULL, position INTEGER NOT NULL, track_path TEXT NOT NULL,
                  PRIMARY KEY(playlist_id,position), FOREIGN KEY(playlist_id) REFERENCES playlists(id) ON DELETE CASCADE);
                CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS playback_session(id INTEGER PRIMARY KEY CHECK(id=1), payload TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS tag_backups(id INTEGER PRIMARY KEY AUTOINCREMENT, original_path TEXT NOT NULL, backup_path TEXT NOT NULL, created_utc TEXT NOT NULL);
                PRAGMA user_version=1;
                """;
            createV1.ExecuteNonQuery();
            transaction.Commit();
            version = 1;
        }
        if (version == 1)
        {
            if (hadDatabaseFile)
            {
                var backup = _databasePath + $".migration-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.bak";
                using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup }.ToString());
                destination.Open();
                connection.BackupDatabase(destination);
            }
            using var transaction = connection.BeginTransaction();
            using var migrate = connection.CreateCommand();
            migrate.Transaction = transaction;
            migrate.CommandText = """
                CREATE VIRTUAL TABLE tracks_fts USING fts5(
                  title, artist, album, album_artist, genre, path,
                  content='tracks', content_rowid='rowid', tokenize='trigram');
                CREATE TRIGGER tracks_fts_insert AFTER INSERT ON tracks BEGIN
                  INSERT INTO tracks_fts(rowid,title,artist,album,album_artist,genre,path)
                  VALUES(new.rowid,new.title,new.artist,new.album,new.album_artist,new.genre,new.path);
                END;
                CREATE TRIGGER tracks_fts_delete AFTER DELETE ON tracks BEGIN
                  INSERT INTO tracks_fts(tracks_fts,rowid,title,artist,album,album_artist,genre,path)
                  VALUES('delete',old.rowid,old.title,old.artist,old.album,old.album_artist,old.genre,old.path);
                END;
                CREATE TRIGGER tracks_fts_update AFTER UPDATE OF title,artist,album,album_artist,genre,path ON tracks BEGIN
                  INSERT INTO tracks_fts(tracks_fts,rowid,title,artist,album,album_artist,genre,path)
                  VALUES('delete',old.rowid,old.title,old.artist,old.album,old.album_artist,old.genre,old.path);
                  INSERT INTO tracks_fts(rowid,title,artist,album,album_artist,genre,path)
                  VALUES(new.rowid,new.title,new.artist,new.album,new.album_artist,new.genre,new.path);
                END;
                INSERT INTO tracks_fts(tracks_fts) VALUES('rebuild');
                CREATE TABLE scan_runs(id INTEGER PRIMARY KEY AUTOINCREMENT, started_utc TEXT NOT NULL);
                CREATE TABLE scan_roots(run_id INTEGER NOT NULL, root_path TEXT NOT NULL, root_prefix TEXT NOT NULL, completed INTEGER NOT NULL DEFAULT 0,
                  PRIMARY KEY(run_id,root_path), FOREIGN KEY(run_id) REFERENCES scan_runs(id) ON DELETE CASCADE);
                CREATE TABLE scan_seen(run_id INTEGER NOT NULL, path TEXT NOT NULL, PRIMARY KEY(run_id,path),
                  FOREIGN KEY(run_id) REFERENCES scan_runs(id) ON DELETE CASCADE);
                CREATE TABLE scan_excluded(run_id INTEGER NOT NULL, path TEXT NOT NULL, prefix TEXT NOT NULL, PRIMARY KEY(run_id,path),
                  FOREIGN KEY(run_id) REFERENCES scan_runs(id) ON DELETE CASCADE);
                CREATE INDEX ix_playlist_tracks_path ON playlist_tracks(playlist_id,track_path,position);
                CREATE INDEX ix_playlist_tracks_path_nocase ON playlist_tracks(playlist_id,track_path COLLATE NOCASE,position);
                CREATE INDEX ix_tracks_artwork_path ON tracks(artwork_path COLLATE NOCASE);
                PRAGMA user_version=2;
                """;
            migrate.ExecuteNonQuery();
            transaction.Commit();
            version = 2;
        }
        if (version == 2)
        {
            // Additive scan bookkeeping: default exclusions can now be pruned after a
            // complete root scan, while user ignores and reparse points remain protected.
            using var transaction = connection.BeginTransaction();
            using var migrate = connection.CreateCommand();
            migrate.Transaction = transaction;
            migrate.CommandText = """
                CREATE TABLE scan_default_excluded(
                  run_id INTEGER NOT NULL, path TEXT NOT NULL, prefix TEXT NOT NULL,
                  PRIMARY KEY(run_id,path), FOREIGN KEY(run_id) REFERENCES scan_runs(id) ON DELETE CASCADE);
                PRAGMA user_version=3;
                """;
            migrate.ExecuteNonQuery();
            transaction.Commit();
            version = 3;
        }
        if (version == 3)
        {
            using var transaction = connection.BeginTransaction();
            using var migrate = connection.CreateCommand();
            migrate.Transaction = transaction;
            migrate.CommandText = """
                CREATE TABLE track_fingerprints(
                  path TEXT PRIMARY KEY,
                  file_size INTEGER NOT NULL,
                  modified_utc TEXT NOT NULL,
                  sha256 TEXT NOT NULL CHECK(length(sha256)=64),
                  FOREIGN KEY(path) REFERENCES tracks(path) ON DELETE CASCADE);
                CREATE INDEX ix_track_fingerprints_sha_size ON track_fingerprints(sha256,file_size);
                PRAGMA user_version=4;
                """;
            migrate.ExecuteNonQuery();
            transaction.Commit();
            version = 4;
        }
        if (version == 4)
        {
            using var transaction = connection.BeginTransaction();
            using var migrate = connection.CreateCommand();
            migrate.Transaction = transaction;
            migrate.CommandText = """
                CREATE TRIGGER track_fingerprints_invalidate_after_track_update
                AFTER UPDATE OF file_size,modified_utc ON tracks
                WHEN old.file_size<>new.file_size OR old.modified_utc<>new.modified_utc
                BEGIN
                  DELETE FROM track_fingerprints WHERE path=new.path;
                END;
                PRAGMA user_version=5;
                """;
            migrate.ExecuteNonQuery();
            transaction.Commit();
            version = 5;
        }
        if (version == 5)
        {
            using var transaction = connection.BeginTransaction();
            using var migrate = connection.CreateCommand();
            migrate.Transaction = transaction;
            migrate.CommandText = "ALTER TABLE tracks ADD COLUMN has_lyrics INTEGER NOT NULL DEFAULT -1 CHECK(has_lyrics IN (-1,0,1)); PRAGMA user_version=6;";
            migrate.ExecuteNonQuery();
            transaction.Commit();
            version = 6;
        }
        if (version == 6)
        {
            using var transaction = connection.BeginTransaction();
            using var migrate = connection.CreateCommand();
            migrate.Transaction = transaction;
            migrate.CommandText = "CREATE TABLE scan_incomplete_paths(run_id INTEGER NOT NULL,path TEXT NOT NULL,prefix TEXT NOT NULL,PRIMARY KEY(run_id,path),FOREIGN KEY(run_id) REFERENCES scan_runs(id) ON DELETE CASCADE); CREATE INDEX ix_scan_incomplete_paths_run ON scan_incomplete_paths(run_id); PRAGMA user_version=7;";
            migrate.ExecuteNonQuery();
            transaction.Commit();
            version = 7;
        }
        if (version == 7)
        {
            using var transaction = connection.BeginTransaction();
            using var migrate = connection.CreateCommand();
            migrate.Transaction = transaction;
            migrate.CommandText = """
                CREATE TABLE track_interaction_state(
                  path TEXT PRIMARY KEY, added_utc TEXT NOT NULL, favorite INTEGER NOT NULL,
                  rating INTEGER NOT NULL, play_count INTEGER NOT NULL, last_played_utc TEXT NULL);
                INSERT INTO track_interaction_state SELECT path,added_utc,favorite,rating,play_count,last_played_utc FROM tracks;
                CREATE TRIGGER tracks_preserve_interactions BEFORE DELETE ON tracks BEGIN
                  INSERT INTO track_interaction_state(path,added_utc,favorite,rating,play_count,last_played_utc)
                  VALUES(old.path,old.added_utc,old.favorite,old.rating,old.play_count,old.last_played_utc)
                  ON CONFLICT(path) DO UPDATE SET added_utc=excluded.added_utc,favorite=excluded.favorite,
                    rating=excluded.rating,play_count=excluded.play_count,last_played_utc=excluded.last_played_utc;
                END;
                CREATE TRIGGER tracks_restore_interactions AFTER INSERT ON tracks
                WHEN EXISTS(SELECT 1 FROM track_interaction_state WHERE path=new.path) BEGIN
                  UPDATE tracks SET (added_utc,favorite,rating,play_count,last_played_utc)=
                    (SELECT added_utc,favorite,rating,play_count,last_played_utc FROM track_interaction_state WHERE path=new.path)
                  WHERE path=new.path;
                END;
                CREATE TABLE track_handles(id TEXT PRIMARY KEY, path TEXT NOT NULL UNIQUE);
                PRAGMA user_version=8;
                """;
            migrate.ExecuteNonQuery();
            for (var offset = 0; ; offset += 512)
            {
                var paths = new List<string>(512);
                using (var read = connection.CreateCommand())
                {
                    read.Transaction = transaction;
                    read.CommandText = "SELECT path FROM tracks ORDER BY rowid LIMIT 512 OFFSET $offset";
                    Add(read, "$offset", offset);
                    using var reader = read.ExecuteReader();
                    while (reader.Read()) paths.Add(reader.GetString(0));
                }
                foreach (var path in paths) RememberTrackPath(connection, transaction, path);
                if (paths.Count < 512) break;
            }
            transaction.Commit();
        }
    }

    private SqliteConnection Open() { var connection = new SqliteConnection(_connectionString); connection.Open(); return connection; }

    private static void BindTrack(SqliteCommand command, Track t)
    {
        Add(command, "$path", Path.GetFullPath(t.Path)); Add(command, "$title", t.Title); Add(command, "$artist", t.Artist); Add(command, "$album", t.Album);
        Add(command, "$albumArtist", t.AlbumArtist); Add(command, "$genre", t.Genre); Add(command, "$year", t.Year); Add(command, "$track", t.TrackNumber);
        Add(command, "$duration", (long)t.Duration.TotalMilliseconds); Add(command, "$size", t.FileSize); Add(command, "$modified", Stamp(t.ModifiedUtc));
        Add(command, "$added", Stamp(t.AddedUtc)); Add(command, "$favorite", t.Favorite ? 1 : 0); Add(command, "$rating", t.Rating);
        Add(command, "$plays", t.PlayCount); Add(command, "$played", t.LastPlayedUtc.HasValue ? Stamp(t.LastPlayedUtc.Value) : DBNull.Value); Add(command, "$artwork", (object?)t.ArtworkPath ?? DBNull.Value);
        Add(command, "$hasLyrics", t.HasLyrics ? 1 : 0);
    }

    private static Track ReadTrack(SqliteDataReader r) => new(
        r.GetString(r.GetOrdinal("path")), r.GetString(r.GetOrdinal("title")), r.GetString(r.GetOrdinal("artist")), r.GetString(r.GetOrdinal("album")),
        r.GetString(r.GetOrdinal("album_artist")), r.GetString(r.GetOrdinal("genre")), (uint)r.GetInt64(r.GetOrdinal("year")), (uint)r.GetInt64(r.GetOrdinal("track_number")),
        TimeSpan.FromMilliseconds(r.GetInt64(r.GetOrdinal("duration_ms"))), r.GetInt64(r.GetOrdinal("file_size")), ParseStamp(r.GetString(r.GetOrdinal("modified_utc"))),
        ParseStamp(r.GetString(r.GetOrdinal("added_utc"))), r.GetInt64(r.GetOrdinal("favorite")) != 0, r.GetInt32(r.GetOrdinal("rating")), r.GetInt32(r.GetOrdinal("play_count")),
        r.IsDBNull(r.GetOrdinal("last_played_utc")) ? null : ParseStamp(r.GetString(r.GetOrdinal("last_played_utc"))),
        r.IsDBNull(r.GetOrdinal("artwork_path")) ? null : r.GetString(r.GetOrdinal("artwork_path")),
        r.GetInt64(r.GetOrdinal("has_lyrics")) == 1);

    private static string Stamp(DateTime value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static string NormalizeRoot(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static string RootPrefix(string root) => Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
    private static string EscapeLike(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
    private static string[] SplitSearchTerms(string? value) => string.IsNullOrWhiteSpace(value)
        ? Array.Empty<string>()
        : value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static string BuildColumnSearchPredicate(string column, IReadOnlyList<string> terms, string parameterPrefix) =>
        terms.Count == 0 ? "1=1" : string.Join(" AND ", terms.Select((_, index) => $"{column} LIKE ${parameterPrefix}{index} ESCAPE '\\'"));
    private static void BindColumnSearch(SqliteCommand command, IReadOnlyList<string> terms, string parameterPrefix)
    {
        for (var index = 0; index < terms.Count; index++)
            Add(command, $"${parameterPrefix}{index}", $"%{EscapeLike(terms[index])}%");
    }
    private static string QuoteFtsTerm(string term) => "\"" + term.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    private static DateTime ParseStamp(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
