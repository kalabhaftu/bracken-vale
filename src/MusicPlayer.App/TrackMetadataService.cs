using BrackenVale.Core;

namespace BrackenVale.App;

/// <summary>
/// Owns file-backed tag and lyrics operations for the application without depending on a window or UI framework.
/// </summary>
internal sealed class TrackMetadataService
{
    private readonly TagEditor _tagEditor;

    public TrackMetadataService(string appDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);
        _tagEditor = new TagEditor(Path.Combine(appDataDirectory, "TagBackups"));
    }

    public async Task SaveLyricsAsync(string trackPath, string text, bool embed, int offsetMilliseconds,
        CancellationToken cancellationToken = default)
    {
        if (offsetMilliseconds != 0)
        {
            var parsed = Lyrics.Parse(text);
            text = parsed.Lines.Count == 0
                ? $"[offset:{offsetMilliseconds}]{Environment.NewLine}{text}"
                : Lyrics.Format(parsed with { Offset = TimeSpan.FromMilliseconds(offsetMilliseconds) });
        }

        if (embed)
            await _tagEditor.SaveAsync(trackPath, new TagEdit(Lyrics: text), cancellationToken: cancellationToken).ConfigureAwait(false);
        else
            LyricsFiles.SaveSidecar(trackPath, text);
    }

    public Task<IReadOnlyList<LyricsSearchResult>> SearchLyricsAsync(string title, string artist,
        CancellationToken cancellationToken = default) =>
        LyricsFiles.SearchLrclibAsync(title, artist, cancellationToken);

    public Task<TagBackup> SaveTagsAsync(string trackPath, TagEdit edit, CancellationToken cancellationToken = default) =>
        _tagEditor.SaveAsync(trackPath, edit, cancellationToken: cancellationToken);

    public IReadOnlyList<TagBackup> ListTagBackups(string trackPath) => _tagEditor.ListBackups(trackPath);

    public Task<TagBackup> RestoreTagsAsync(TagBackup backup, CancellationToken cancellationToken = default) =>
        _tagEditor.RestoreAsync(backup, cancellationToken: cancellationToken);
}
