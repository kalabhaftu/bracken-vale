using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace BrackenVale.Core;

public enum TrackSort { Title, Artist, Album, Genre, Year, Added, Duration, PlayCount, LastPlayed, Path, Rating }
public sealed record IndexedFileState(long Length, DateTime ModifiedUtc);

public sealed class LibraryStore
{
    private const int SchemaVersion = 1;
    private readonly string _databasePath;
    private readonly string _connectionString;

    public LibraryStore(string databasePath)
    {
        _databasePath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _databasePath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared, DefaultTimeout = 10 }.ToString();
        Migrate();
    }

    public static LibraryStore InAppData()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new(Path.Combine(root, "BrackenVale", "library.db"));
    }

    public void UpsertTrack(Track track)
    {
        using var connection = Open();
        Upsert(connection, null, track);
    }

    public void UpsertTracks(IEnumerable<Track> tracks)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (var track in tracks) Upsert(connection, transaction, track);
        transaction.Commit();
    }

    public void RemoveTracks(IEnumerable<string> paths)
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
    }

    private static void Upsert(SqliteConnection connection, SqliteTransaction? transaction, Track track)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO tracks(path,title,artist,album,album_artist,genre,year,track_number,duration_ms,file_size,modified_utc,added_utc,favorite,rating,play_count,last_played_utc,artwork_path)
            VALUES($path,$title,$artist,$album,$albumArtist,$genre,$year,$track,$duration,$size,$modified,$added,$favorite,$rating,$plays,$played,$artwork)
            ON CONFLICT(path) DO UPDATE SET title=excluded.title,artist=excluded.artist,album=excluded.album,album_artist=excluded.album_artist,
              genre=excluded.genre,year=excluded.year,track_number=excluded.track_number,duration_ms=excluded.duration_ms,file_size=excluded.file_size,
              modified_utc=excluded.modified_utc,artwork_path=excluded.artwork_path
            """;
        BindTrack(command, track);
        command.ExecuteNonQuery();
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

    public IReadOnlyList<Track> GetTracks(string? search = null, TrackSort sort = TrackSort.Title, bool descending = false, string? filter = null, string? groupColumn = null, string? groupValue = null)
    {
        var orderBy = sort switch
        {
            TrackSort.Artist => "artist", TrackSort.Album => "album", TrackSort.Genre => "genre", TrackSort.Year => "year",
            TrackSort.Added => "added_utc", TrackSort.Duration => "duration_ms", TrackSort.PlayCount => "play_count",
            TrackSort.LastPlayed => "last_played_utc", TrackSort.Path => "path", TrackSort.Rating => "rating", _ => "title"
        };
        var predicate = filter switch
        {
            "favorites" => "favorite=1", "most-played" => "play_count>0", "recent" => "last_played_utc IS NOT NULL",
            _ => "1=1"
        };
        var groupPredicate = groupColumn switch
        {
            "album" => "($group IS NULL OR album=$group)", "artist" => "($group IS NULL OR artist=$group)",
            "genre" => "($group IS NULL OR genre=$group)", "folder" => "($folderPrefix IS NULL OR path LIKE $folderPrefix ESCAPE '\\')",
            null => "1=1", _ => throw new ArgumentOutOfRangeException(nameof(groupColumn))
        };
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM tracks WHERE {predicate} AND {groupPredicate} AND ($search IS NULL OR title LIKE $pattern ESCAPE '\\' OR artist LIKE $pattern ESCAPE '\\' OR album LIKE $pattern ESCAPE '\\' OR album_artist LIKE $pattern ESCAPE '\\' OR genre LIKE $pattern ESCAPE '\\' OR path LIKE $pattern ESCAPE '\\') ORDER BY {orderBy} {(descending ? "DESC" : "ASC")}, title COLLATE NOCASE";
        Add(command, "$search", string.IsNullOrWhiteSpace(search) ? DBNull.Value : search.Trim());
        var escaped = search?.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
        Add(command, "$pattern", string.IsNullOrWhiteSpace(escaped) ? DBNull.Value : $"%{escaped}%");
        Add(command, "$group", string.IsNullOrWhiteSpace(groupValue) || groupColumn == "folder" ? DBNull.Value : groupValue);
        string? folderPrefix = null;
        if (groupColumn == "folder" && !string.IsNullOrWhiteSpace(groupValue))
        {
            var folder = Path.GetFullPath(groupValue);
            var prefix = Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar;
            folderPrefix = EscapeLike(prefix) + "%";
        }
        Add(command, "$folderPrefix", folderPrefix is null ? DBNull.Value : folderPrefix);
        using var reader = command.ExecuteReader();
        var result = new List<Track>();
        while (reader.Read()) result.Add(ReadTrack(reader));
        return result;
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
        return command.ExecuteScalar() is string payload ? JsonSerializer.Deserialize<PlaybackSession>(payload) : null;
    }

    public void RecordTagBackup(TagBackup backup)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO tag_backups(original_path,backup_path,created_utc) VALUES($path,$backup,$created)";
        Add(command, "$path", Path.GetFullPath(backup.OriginalPath)); Add(command, "$backup", Path.GetFullPath(backup.BackupPath)); Add(command, "$created", Stamp(backup.CreatedUtc));
        command.ExecuteNonQuery();
    }

    public TagBackup? GetLatestTagBackup(string path)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT original_path,backup_path,created_utc FROM tag_backups WHERE original_path=$path ORDER BY id DESC LIMIT 1";
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
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        using (var remove = connection.CreateCommand()) { remove.Transaction = transaction; remove.CommandText = "DELETE FROM playlist_tracks WHERE playlist_id=$id AND position=$position"; Add(remove, "$id", id); Add(remove, "$position", position); remove.ExecuteNonQuery(); }
        using (var reorder = connection.CreateCommand()) { reorder.Transaction = transaction; reorder.CommandText = "UPDATE playlist_tracks SET position=position-1 WHERE playlist_id=$id AND position>$position"; Add(reorder, "$id", id); Add(reorder, "$position", position); reorder.ExecuteNonQuery(); }
        transaction.Commit();
    }

    public void ImportM3u8(string playlistFile, string? name = null) => CreatePlaylist(name ?? Path.GetFileNameWithoutExtension(playlistFile), Playlists.ReadM3u8(playlistFile));
    public void ExportM3u8(string playlistId, string destination)
    {
        var playlist = GetPlaylists().SingleOrDefault(item => item.Id == playlistId) ?? throw new KeyNotFoundException("Playlist not found.");
        Playlists.WriteM3u8(destination, playlist.Paths);
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
        using var connection = Open();
        using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(versionCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (version > SchemaVersion) throw new InvalidDataException($"Database version {version} is newer than this app supports.");
        if (version > 0 && version < SchemaVersion && System.IO.File.Exists(_databasePath))
        {
            var backup = _databasePath + $".migration-{DateTime.UtcNow:yyyyMMdd-HHmmss}.bak";
            using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup }.ToString());
            destination.Open(); connection.BackupDatabase(destination);
        }
        using var command = connection.CreateCommand();
        command.CommandText = """
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
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open() { var connection = new SqliteConnection(_connectionString); connection.Open(); return connection; }

    private static void BindTrack(SqliteCommand command, Track t)
    {
        Add(command, "$path", Path.GetFullPath(t.Path)); Add(command, "$title", t.Title); Add(command, "$artist", t.Artist); Add(command, "$album", t.Album);
        Add(command, "$albumArtist", t.AlbumArtist); Add(command, "$genre", t.Genre); Add(command, "$year", t.Year); Add(command, "$track", t.TrackNumber);
        Add(command, "$duration", (long)t.Duration.TotalMilliseconds); Add(command, "$size", t.FileSize); Add(command, "$modified", Stamp(t.ModifiedUtc));
        Add(command, "$added", Stamp(t.AddedUtc)); Add(command, "$favorite", t.Favorite ? 1 : 0); Add(command, "$rating", t.Rating);
        Add(command, "$plays", t.PlayCount); Add(command, "$played", t.LastPlayedUtc.HasValue ? Stamp(t.LastPlayedUtc.Value) : DBNull.Value); Add(command, "$artwork", (object?)t.ArtworkPath ?? DBNull.Value);
    }

    private static Track ReadTrack(SqliteDataReader r) => new(
        r.GetString(r.GetOrdinal("path")), r.GetString(r.GetOrdinal("title")), r.GetString(r.GetOrdinal("artist")), r.GetString(r.GetOrdinal("album")),
        r.GetString(r.GetOrdinal("album_artist")), r.GetString(r.GetOrdinal("genre")), (uint)r.GetInt64(r.GetOrdinal("year")), (uint)r.GetInt64(r.GetOrdinal("track_number")),
        TimeSpan.FromMilliseconds(r.GetInt64(r.GetOrdinal("duration_ms"))), r.GetInt64(r.GetOrdinal("file_size")), ParseStamp(r.GetString(r.GetOrdinal("modified_utc"))),
        ParseStamp(r.GetString(r.GetOrdinal("added_utc"))), r.GetInt64(r.GetOrdinal("favorite")) != 0, r.GetInt32(r.GetOrdinal("rating")), r.GetInt32(r.GetOrdinal("play_count")),
        r.IsDBNull(r.GetOrdinal("last_played_utc")) ? null : ParseStamp(r.GetString(r.GetOrdinal("last_played_utc"))),
        r.IsDBNull(r.GetOrdinal("artwork_path")) ? null : r.GetString(r.GetOrdinal("artwork_path")));

    private static string Stamp(DateTime value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static string EscapeLike(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
    private static DateTime ParseStamp(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
