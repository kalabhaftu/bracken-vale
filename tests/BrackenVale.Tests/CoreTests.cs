using BrackenVale.Core;
using Microsoft.Data.Sqlite;
using TagLib;
using Xunit;

namespace BrackenVale.Tests;

public sealed class CoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bracken-vale-tests-" + Guid.NewGuid().ToString("N"));
    private readonly LibraryStore _store;

    public CoreTests()
    {
        Directory.CreateDirectory(_root);
        _store = new(Path.Combine(_root, "library.db"));
    }

    [Fact]
    public void Local_log_saves_error_details_in_an_easy_to_open_folder()
    {
        var folder = Path.Combine(_root, "Logs");
        var log = new LocalAppLog(folder);
        log.Error("tag-editor", "Could not save tags.", new IOException("file is read-only"));
        var file = Assert.Single(Directory.GetFiles(folder, "bracken-vale-*.log"));
        var contents = System.IO.File.ReadAllText(file);
        Assert.Contains("[ERROR] [tag-editor] Could not save tags.", contents);
        Assert.Contains("System.IO.IOException: file is read-only", contents);
    }

    [Fact]
    public void Local_log_rotates_and_caps_large_entries()
    {
        var folder = Path.Combine(_root, "RotatingLogs");
        var log = new LocalAppLog(folder);
        log.Info("test", new string('x', 4 * 1024 * 1024 + 100));
        log.Info("test", "after rotation");
        var rotated = Assert.Single(Directory.GetFiles(folder, "bracken-vale-*.log.1"));
        var current = Assert.Single(Directory.GetFiles(folder, "bracken-vale-*.log"));
        Assert.True(new FileInfo(rotated).Length <= 4 * 1024 * 1024);
        Assert.Contains("after rotation", System.IO.File.ReadAllText(current));
    }

    [Fact]
    public async Task Scanner_skips_ignored_directories_and_symlinks()
    {
        var root = Path.Combine(_root, "music"); var ignored = Path.Combine(root, "skip"); var linked = Path.Combine(root, "linked");
        var system = Path.Combine(root, "other-user", "AppData");
        var external = Path.Combine(_root, "external");
        Directory.CreateDirectory(ignored); Directory.CreateDirectory(external); Directory.CreateDirectory(system); Directory.CreateDirectory(root);
        await System.IO.File.WriteAllTextAsync(Path.Combine(root, "keep.mp3"), "metadata is irrelevant to enumeration");
        await System.IO.File.WriteAllTextAsync(Path.Combine(root, "readme.txt"), "ignore extension");
        await System.IO.File.WriteAllTextAsync(Path.Combine(ignored, "ignored.flac"), "ignore folder");
        await System.IO.File.WriteAllTextAsync(Path.Combine(system, "system.mp3"), "exclude application data");
        await System.IO.File.WriteAllTextAsync(Path.Combine(external, "linked.wav"), "skip linked directory");
        Directory.CreateSymbolicLink(linked, external);
        var found = new List<string>();
        using var control = new ScanControl();
        await new LibraryScanner(new LocalAppLog(Path.Combine(_root, "Logs"))).ScanAsync([root], [ignored], control,
            (path, _) => { found.Add(Path.GetFullPath(path)); return ValueTask.CompletedTask; });
        Assert.Equal([Path.Combine(root, "keep.mp3")], found);
    }

    [Fact]
    public async Task Scanner_cancellation_interrupts_a_paused_scan()
    {
        var root = Path.Combine(_root, "pause"); Directory.CreateDirectory(root);
        using var control = new ScanControl(); control.Pause();
        var scan = new LibraryScanner(new LocalAppLog(Path.Combine(_root, "Logs"))).ScanAsync([root], [], control, (_, _) => ValueTask.CompletedTask);
        await Task.Delay(50); control.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
    }

    [Fact]
    public async Task Scanner_skips_unavailable_roots()
    {
        var found = new List<string>();
        using var control = new ScanControl();
        await new LibraryScanner(new LocalAppLog(Path.Combine(_root, "Logs"))).ScanAsync([Path.Combine(_root, "missing-root")], [], control,
            (path, _) => { found.Add(path); return ValueTask.CompletedTask; });
        Assert.Empty(found);
    }

    [Fact]
    public async Task Indexer_removes_deleted_tracks_but_preserves_unavailable_roots()
    {
        var root = Path.Combine(_root, "available");
        Directory.CreateDirectory(root);
        var deleted = Path.Combine(root, "deleted.mp3");
        var offline = Path.Combine(_root, "offline", "not-mounted.flac");
        _store.UpsertTracks([MakeTrack("deleted", "Artist", deleted), MakeTrack("offline", "Artist", offline)]);
        using var control = new ScanControl();
        var result = await new LibraryIndexer(_store, Path.Combine(_root, "artwork"), new LocalAppLog(Path.Combine(_root, "Logs")))
            .ScanAsync([root, Path.GetDirectoryName(offline)!], [], control);
        Assert.Equal(1, result.Removed);
        Assert.Null(_store.GetTrack(deleted));
        Assert.NotNull(_store.GetTrack(offline));
    }

    [Fact]
    public void Search_sort_rating_favorite_play_count_and_session_round_trip()
    {
        var a = MakeTrack("zeta", "June", "/library/one.flac");
        var b = MakeTrack("alpha", "Noah", "/library/two.mp3");
        _store.UpsertTracks([a, b]);
        _store.SetFavorite(a.Path, true); _store.SetRating(a.Path, 5); _store.RecordPlayed(a.Path, DateTime.UtcNow);
        _store.SetSetting("theme", "Dark");
        var session = new PlaybackSession(a.Path, 1234, [a.Path, b.Path], true, "Queue");
        _store.SaveSession(session);

        Assert.Equal("Dark", _store.GetSetting("theme"));
        Assert.Equal(5, _store.GetTracks(filter: "favorites").Single().Rating);
        Assert.Equal(1, _store.GetTracks(filter: "most-played").Single().PlayCount);
        Assert.Equal("alpha", _store.GetTracks(sort: TrackSort.Title).First().Title);
        Assert.Equal(a.Path, _store.GetTracks(search: "one.flac").Single().Path);
        var restored = Assert.IsType<PlaybackSession>(_store.LoadSession());
        Assert.Equal(session.TrackPath, restored.TrackPath); Assert.Equal(session.PositionMilliseconds, restored.PositionMilliseconds);
        Assert.Equal(session.Queue, restored.Queue); Assert.Equal(session.Shuffle, restored.Shuffle); Assert.Equal(session.RepeatMode, restored.RepeatMode);
        Assert.True(PlayCompletion.HasReachedHalf(TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(50)));
        Assert.False(PlayCompletion.HasReachedHalf(TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(49)));
        Assert.False(PlayCompletion.HasReachedHalf(TimeSpan.Zero, TimeSpan.FromSeconds(4)));
        Assert.Throws<ArgumentOutOfRangeException>(() => _store.SetRating(a.Path, 6));
    }

    [Fact]
    public void Album_artist_genre_and_folder_groups_filter_tracks()
    {
        var rock = MakeTrack("song", "June", Path.Combine(_root, "library", "Rock", "song.flac"));
        var pop = MakeTrack("song", "Noah", Path.Combine(_root, "library", "Pop", "song.mp3"));
        _store.UpsertTracks([rock with { Genre = "Rock" }, pop with { Genre = "Pop", Album = "Other Album" }]);
        Assert.Equal([rock.Path], _store.GetTracks(groupColumn: "album", groupValue: "Album").Select(track => track.Path));
        Assert.Equal([pop.Path], _store.GetTracks(groupColumn: "artist", groupValue: "Noah").Select(track => track.Path));
        Assert.Equal([rock.Path], _store.GetTracks(groupColumn: "genre", groupValue: "Rock").Select(track => track.Path));
        Assert.Equal([rock.Path], _store.GetTracks(groupColumn: "folder", groupValue: Path.Combine(_root, "library", "Rock")).Select(track => track.Path));
        Assert.Contains(Path.Combine(_root, "library", "Rock"), _store.GetFolders());
    }

    [Fact]
    public void M3u8_round_trip_preserves_order_and_tracks_across_directories()
    {
        var one = Path.Combine(_root, "albums", "one", "one.flac");
        var two = Path.Combine(_root, "compilations", "two.mp3");
        var playlist = Path.Combine(_root, "playlists", "Road.m3u8");
        var imported = Path.Combine(_root, "playlists", "Imported.m3u8");
        var created = _store.CreatePlaylist("Road", [one, two]);
        _store.ExportM3u8(created.Id, playlist);
        Assert.Equal([one, two], Playlists.ReadM3u8(playlist));
        _store.ImportM3u8(playlist, "Imported");
        var loaded = _store.GetPlaylists().Single(item => item.Name == "Imported");
        Assert.Equal([one, two], loaded.Paths);
        _store.RemoveFromPlaylist(loaded.Id, 0);
        Assert.Equal([two], _store.GetPlaylists().Single(item => item.Id == loaded.Id).Paths);
        _store.RenamePlaylist(loaded.Id, "Road copy");
        Assert.Equal("Road copy", _store.GetPlaylists().Single(item => item.Id == loaded.Id).Name);
        _store.DeletePlaylist(loaded.Id);
        Assert.DoesNotContain(_store.GetPlaylists(), item => item.Id == loaded.Id);
    }

    [Fact]
    public void Lrc_parser_keeps_milliseconds_multiple_timestamps_and_offset()
    {
        var document = Lyrics.Parse("[offset:250]\n[00:01.25][00:02.50]First\n[00:04.00]Second\n");
        Assert.Equal(TimeSpan.FromMilliseconds(1250), document.Lines[0].Time);
        Assert.Equal(TimeSpan.FromMilliseconds(2500), document.Lines[1].Time);
        Assert.Equal("First", document.At(TimeSpan.FromMilliseconds(1500)));
        Assert.Equal("Second", document.At(TimeSpan.FromSeconds(5)));
        var reparsed = Lyrics.Parse(Lyrics.Format(document));
        Assert.Equal(document.Lines, reparsed.Lines);
        Assert.Equal(document.Offset, reparsed.Offset);
        var lyricsPath = Path.Combine(_root, "timed.lrc");
        LyricsFiles.SaveSidecar(Path.Combine(_root, "timed.flac"), "[ar:Artist]\n[offset:250]\n[00:01.25]Café\n");
        Assert.Equal("[ar:Artist]\n[offset:250]\n[00:01.25]Café\n", System.IO.File.ReadAllText(lyricsPath));
    }

    [Fact]
    public void Tag_save_writes_recoverable_backup_and_restore_recovers_original_bytes()
    {
        var path = Path.Combine(_root, "tone.wav");
        WriteWave(path);
        var original = System.IO.File.ReadAllBytes(path);
        var editor = new TagEditor(Path.Combine(_root, "backups"));
        var backup = editor.Save(path, new TagEdit(Title: "Edited title", Artist: "Sample artist"));
        Assert.NotEqual(Convert.ToHexString(original), Convert.ToHexString(System.IO.File.ReadAllBytes(path)));
        using (var media = TagLib.File.Create(path)) Assert.Equal("Edited title", media.Tag.Title);
        Assert.True(System.IO.File.Exists(backup.BackupPath));
        editor.Restore(backup);
        Assert.Equal(original, System.IO.File.ReadAllBytes(path));
    }

    private static Track MakeTrack(string title, string artist, string path) => new(
        Path.GetFullPath(path), title, artist, "Album", artist, "Jazz", 2022, 1, TimeSpan.FromSeconds(210), 1234,
        DateTime.UtcNow, DateTime.UtcNow);

    private static void WriteWave(string path)
    {
        const int sampleRate = 44100, channels = 2, bits = 16, dataLength = 4;
        using var writer = new BinaryWriter(System.IO.File.Create(path));
        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + dataLength); writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)channels);
        writer.Write(sampleRate); writer.Write(sampleRate * channels * bits / 8); writer.Write((short)(channels * bits / 8)); writer.Write((short)bits);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(dataLength); writer.Write(new byte[dataLength]);
    }

    public void Dispose()
    {
        // Release idle pooled database handles before deleting the temporary files on Windows.
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
