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
        var customWindowsFolder = Path.Combine(root, "Windows");
        var external = Path.Combine(_root, "external");
        Directory.CreateDirectory(ignored); Directory.CreateDirectory(external); Directory.CreateDirectory(system); Directory.CreateDirectory(customWindowsFolder); Directory.CreateDirectory(root);
        await System.IO.File.WriteAllTextAsync(Path.Combine(root, "keep.mp3"), "metadata is irrelevant to enumeration");
        await System.IO.File.WriteAllTextAsync(Path.Combine(root, "spoken.m4b"), "audiobook extension");
        await System.IO.File.WriteAllTextAsync(Path.Combine(root, "readme.txt"), "ignore extension");
        await System.IO.File.WriteAllTextAsync(Path.Combine(ignored, "ignored.flac"), "ignore folder");
        await System.IO.File.WriteAllTextAsync(Path.Combine(system, "system.mp3"), "exclude application data");
        await System.IO.File.WriteAllTextAsync(Path.Combine(customWindowsFolder, "user-music.flac"), "a user folder can share a system folder name");
        await System.IO.File.WriteAllTextAsync(Path.Combine(external, "linked.wav"), "skip linked directory");
        Directory.CreateSymbolicLink(linked, external);
        var found = new List<string>();
        using var control = new ScanControl();
        await new LibraryScanner(new LocalAppLog(Path.Combine(_root, "Logs"))).ScanAsync([root], [ignored + Path.DirectorySeparatorChar], control,
            (path, _) => { found.Add(Path.GetFullPath(path)); return ValueTask.CompletedTask; });
        Assert.Equal(3, found.Count);
        Assert.Contains(Path.Combine(root, "keep.mp3"), found);
        Assert.Contains(Path.Combine(root, "spoken.m4b"), found);
        Assert.Contains(Path.Combine(customWindowsFolder, "user-music.flac"), found);
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
        var b = MakeTrack("alpha", "Noah", "/library/two.mp3") with { LastPlayedUtc = DateTime.UtcNow.AddDays(-1) };
        _store.UpsertTracks([a, b]);
        _store.SetFavorite(a.Path, true); _store.SetRating(a.Path, 5); _store.RecordPlayed(a.Path, DateTime.UtcNow);
        _store.SetRating(b.Path, 2);
        _store.SetSetting("theme", "Dark");
        var session = new PlaybackSession(a.Path, 1234, [a.Path, b.Path], true, "Queue");
        _store.SaveSession(session);

        Assert.Equal("Dark", _store.GetSetting("theme"));
        Assert.Equal(5, _store.GetTracks(filter: "favorites").Single().Rating);
        Assert.Equal(1, _store.GetTracks(filter: "most-played").Single().PlayCount);
        Assert.Equal("alpha", _store.GetTracks(sort: TrackSort.Title).First().Title);
        Assert.Equal(b.Path, _store.GetTracks(sort: TrackSort.Rating).First().Path);
        Assert.Equal(a.Path, _store.GetTracks(sort: TrackSort.LastPlayed, descending: true).First().Path);
        Assert.Equal(a.Path, _store.GetTracks(sort: TrackSort.Path).First().Path);
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
        var created = _store.CreatePlaylist("Road", [one, two, one]);
        _store.ExportM3u8(created.Id, playlist);
        Assert.Equal([one, two, one], Playlists.ReadM3u8(playlist));
        _store.ImportM3u8(playlist, "Imported");
        var loaded = _store.GetPlaylists().Single(item => item.Name == "Imported");
        Assert.Equal([one, two, one], loaded.Paths);
        _store.RemoveFromPlaylist(loaded.Id, 0);
        Assert.Equal([two, one], _store.GetPlaylists().Single(item => item.Id == loaded.Id).Paths);
        _store.RenamePlaylist(loaded.Id, "Road copy");
        Assert.Equal("Road copy", _store.GetPlaylists().Single(item => item.Id == loaded.Id).Name);
        _store.DeletePlaylist(loaded.Id);
        Assert.DoesNotContain(_store.GetPlaylists(), item => item.Id == loaded.Id);
    }

    [Fact]
    public void Playlist_duplicate_path_occurrences_map_to_their_distinct_positions()
    {
        var paths = new[] { "missing.flac", "same.mp3", "unindexed.wav", "same.mp3" };
        Assert.Equal(1, Playlists.FindPathOccurrence(paths, "same.mp3", 0));
        Assert.Equal(3, Playlists.FindPathOccurrence(paths, "same.mp3", 1));
        Assert.Equal(-1, Playlists.FindPathOccurrence(paths, "same.mp3", 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => Playlists.FindPathOccurrence(paths, "same.mp3", -1));
    }

    [Fact]
    public void Queue_navigation_respects_repeat_modes_and_shuffles_only_upcoming_tracks()
    {
        Assert.Equal(0, QueueNavigation.NextIndex(4, -1, false, "Off"));
        Assert.Equal(2, QueueNavigation.NextIndex(4, 1, true, "Off"));
        Assert.Equal(-1, QueueNavigation.NextIndex(4, 3, true, "Off"));
        Assert.Equal(0, QueueNavigation.NextIndex(4, 3, true, "Queue"));
        Assert.Equal(2, QueueNavigation.NextIndex(4, 2, true, "Track"));
        Assert.Equal(3, QueueNavigation.NextIndex(4, 2, false, "Track"));
        Assert.Equal(-1, QueueNavigation.NextIndex(0, -1, false, "Queue"));

        var queue = new[] { "heard", "playing", "one", "two", "three" };
        QueueNavigation.ShuffleUpcoming(queue, 2);
        Assert.Equal(["heard", "playing"], queue[..2]);
        Assert.Equal(new[] { "one", "two", "three" }, queue[2..].OrderBy(value => value, StringComparer.Ordinal));
        Assert.Throws<ArgumentOutOfRangeException>(() => QueueNavigation.ShuffleUpcoming(queue, queue.Length + 1));
    }

    [Fact]
    public void Lrc_parser_keeps_milliseconds_multiple_timestamps_and_offset()
    {
        var document = Lyrics.Parse("[offset:250]\n[00:01.25][00:02.50]First\n[00:04.00]Second\n");
        Assert.Equal(TimeSpan.FromMilliseconds(1250), document.Lines[0].Time);
        Assert.Equal(TimeSpan.FromMilliseconds(2500), document.Lines[1].Time);
        Assert.Equal(string.Empty, document.At(TimeSpan.Zero));
        Assert.Equal("First", document.At(TimeSpan.FromMilliseconds(1500)));
        Assert.Equal("Second", document.At(TimeSpan.FromSeconds(5)));
        const string plainLyrics = "First plain line\nSecond plain line\n";
        Assert.Equal(plainLyrics, Lyrics.DisplayAt(Lyrics.Parse(plainLyrics), plainLyrics, TimeSpan.Zero));
        Assert.Equal("First", Lyrics.DisplayAt(document, plainLyrics, TimeSpan.FromMilliseconds(1500)));
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

    [Fact]
    public void Tag_editor_round_trips_and_removes_id3v2_custom_fields()
    {
        var path = Path.Combine(_root, "custom.wav");
        WriteWave(path);
        var editor = new TagEditor(Path.Combine(_root, "backups"));
        var initial = new Dictionary<string, string> { ["MOOD"] = "warm", ["SOURCE"] = "vinyl", ["ID3:TLAN"] = "eng" };

        editor.Save(path, new TagEdit(CustomFields: initial));
        Assert.Equal("ID3v2 text frames and user text", TagEditor.CustomFieldFormat(path));
        Assert.Equal(initial, TagEditor.ReadCustomFields(path));

        var updated = new Dictionary<string, string> { ["MOOD"] = "quiet", ["ID3:TLAN"] = "fra" };
        editor.Save(path, new TagEdit(CustomFields: updated));
        Assert.Equal(updated, TagEditor.ReadCustomFields(path));
    }

    [Fact]
    public void Tag_editor_round_trips_additional_standard_fields_and_keeps_original_on_invalid_values()
    {
        var path = Path.Combine(_root, "extended.wav");
        WriteWave(path);
        var editor = new TagEditor(Path.Combine(_root, "backups"));
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["COMPOSERS"] = "Ludwig; Wolfgang", ["COMMENT"] = "Studio master", ["TRACK_COUNT"] = "12",
            ["DISC"] = "2", ["DISC_COUNT"] = "3", ["BPM"] = "120", ["GROUPING"] = "Suite",
            ["PUBLISHER"] = "Label", ["INITIAL_KEY"] = "C minor", ["ISRC"] = "USAAA2400001",
            ["MUSICBRAINZ_TRACK_ID"] = "recording-id", ["REPLAYGAIN_TRACK_GAIN"] = "-6.25"
        };
        editor.Save(path, new TagEdit(AdditionalFields: fields));
        var actual = TagEditor.ReadAdditionalStandardFields(path);
        foreach (var (key, value) in fields) Assert.Equal(value, actual[key]);

        var original = System.IO.File.ReadAllBytes(path);
        Assert.Throws<ArgumentException>(() => editor.Save(path, new TagEdit(AdditionalFields: new Dictionary<string, string> { ["BPM"] = "fast" })));
        Assert.Equal(original, System.IO.File.ReadAllBytes(path));
    }

    [Fact]
    public void Track_reader_cache_changes_when_embedded_artwork_changes()
    {
        var path = Path.Combine(_root, "cover.wav");
        var firstArt = Path.Combine(_root, "first.png"); var secondArt = Path.Combine(_root, "second.png");
        var cache = Path.Combine(_root, "artwork");
        WriteWave(path);
        System.IO.File.WriteAllBytes(firstArt, [1, 2, 3]); System.IO.File.WriteAllBytes(secondArt, [4, 5, 6]);
        var editor = new TagEditor(Path.Combine(_root, "backups"));
        editor.Save(path, new TagEdit(ArtworkPath: firstArt));
        var first = TrackReader.Read(path, cache);
        editor.Save(path, new TagEdit(ArtworkPath: secondArt));
        var second = TrackReader.Read(path, cache);

        Assert.NotEqual(first.ArtworkPath, second.ArtworkPath);
        Assert.Equal(System.IO.File.ReadAllBytes(secondArt), System.IO.File.ReadAllBytes(second.ArtworkPath!));
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
