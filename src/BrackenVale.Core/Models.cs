namespace BrackenVale.Core;

public sealed record Track(
    string Path,
    string Title,
    string Artist,
    string Album,
    string AlbumArtist,
    string Genre,
    uint Year,
    uint TrackNumber,
    TimeSpan Duration,
    long FileSize,
    DateTime ModifiedUtc,
    DateTime AddedUtc,
    bool Favorite = false,
    int Rating = 0,
    int PlayCount = 0,
    DateTime? LastPlayedUtc = null,
    string? ArtworkPath = null)
{
    public string RatingDisplay => Rating == 0 ? "☆" : new string('★', Rating);
    public string FavoriteGlyph => Favorite ? "♥" : "♡";
}

public sealed record ScanProgress(int FilesFound, int DirectoriesVisited, string CurrentPath);
public sealed record LyricsLine(TimeSpan Time, string Text);
public sealed record LyricsDocument(IReadOnlyList<LyricsLine> Lines, TimeSpan Offset)
{
    public string At(TimeSpan position)
    {
        var time = position - Offset;
        for (var i = Lines.Count - 1; i >= 0; i--)
            if (Lines[i].Time <= time) return Lines[i].Text;
        return string.Empty;
    }
}

public sealed record Playlist(string Id, string Name, IReadOnlyList<string> Paths, DateTime CreatedUtc);
public sealed record PlaybackSession(string? TrackPath, long PositionMilliseconds, IReadOnlyList<string> Queue, bool Shuffle, string RepeatMode);
public sealed record TagEdit(
    string? Title = null,
    string? Artist = null,
    string? Album = null,
    string? AlbumArtist = null,
    string? Genre = null,
    uint? Year = null,
    uint? TrackNumber = null,
    string? Lyrics = null,
    string? ArtworkPath = null,
    IReadOnlyDictionary<string, string>? CustomFields = null,
    IReadOnlyDictionary<string, string>? AdditionalFields = null);

public sealed record TagBackup(string OriginalPath, string BackupPath, DateTime CreatedUtc);
