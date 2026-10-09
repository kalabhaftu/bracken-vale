using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MusicPlayer.Core;

var root = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "music-player-benchmark-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
var results = new List<object>();
foreach (var count in new[] { 100, 100000 })
{
    var path = Path.Combine(root, $"library-{count}.db");
    if (File.Exists(path)) throw new IOException("Use an empty benchmark output directory.");
    var store = new LibraryStore(path, new LocalAppLog(Path.Combine(root, "Logs")));
    var now = DateTime.UtcNow;
    var queue = Enumerable.Range(0, count).Select(i => Path.Combine(root, "media", $"track-{i}.wav")).ToArray();
    for (var offset = 0; offset < count; offset += 512)
        store.UpsertTracks(queue.Skip(offset).Take(512).Select((file, index) => new Track(file, $"Track {offset + index:D6}", "Artist", "Album", "Artist", "Genre", 2026, 1, TimeSpan.FromSeconds(5), 1000, now, now)));
    var session = new PlaybackSession(queue[0], 1000, queue, false, "Off", QueueIndex: 0);
    store.SaveSession(session);
    using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, DefaultTimeout = 30, ForeignKeys = true }.ToString());
    connection.Open();
    void Execute(string sql)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    Execute("PRAGMA wal_checkpoint(TRUNCATE)");
    var timer = Stopwatch.StartNew();
    // Exact pre-v9 algorithm: serialize and upsert the full session JSON.
    using (var legacy = connection.CreateCommand())
    {
        legacy.CommandText = "INSERT INTO playback_session(id,payload) VALUES(1,$payload) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload";
        legacy.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(session));
        legacy.ExecuteNonQuery();
    }
    var legacyMs = timer.Elapsed.TotalMilliseconds;
    var legacyWalBytes = new FileInfo(path + "-wal").Length;
    store.SaveSession(session);
    Execute("PRAGMA wal_checkpoint(TRUNCATE)");
    timer.Restart();
    store.SaveSessionState(session with { PositionMilliseconds = 2000, Queue = [] });
    var stateMs = timer.Elapsed.TotalMilliseconds;
    var stateWalBytes = new FileInfo(path + "-wal").Length;
    timer.Restart();
    var rows = store.GetTracksPage(pageSize: 100);
    var pageMs = timer.Elapsed.TotalMilliseconds;
    if (rows.Count != Math.Min(count, 100)) throw new InvalidDataException("Fixture paging failed.");
    await store.CheckpointWalAsync();
    results.Add(new { tracks = count, legacyCheckpointMs = legacyMs, stateCheckpointMs = stateMs, legacyWalBytes, stateWalBytes, firstPageMs = pageMs });
}
var json = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(Path.Combine(root, "results.json"), json);
Console.WriteLine(json);
