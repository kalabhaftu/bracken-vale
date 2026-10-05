using BrackenVale.Core;

namespace BrackenVale.App;

public sealed record PlaylistTrackRow(PlaylistEntry Entry)
{
    public string Title => Entry.Track?.Title ?? Path.GetFileNameWithoutExtension(Entry.Path);
    public string Artist => Entry.Track?.Artist ?? "Unavailable";
    public string Album => Entry.Track?.Album ?? "";
    public string TrackPath => Entry.Path;
    public string Duration => Entry.Track is { } track ? track.Duration.ToString(@"m\:ss") : "—";
    public string? ArtworkPath => Entry.Track?.ArtworkPath;
    public string AccessibleName => Entry.Track is { } track
        ? $"{track.Title}, {track.Artist}, {track.Album}"
        : $"Unavailable track, {Path.GetFileNameWithoutExtension(Entry.Path)}, {Entry.Path}";
}

internal sealed record TagBackupOption(TagBackup Backup, string Label);
