using MusicPlayer.Core;

namespace MusicPlayer.App;

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
        var parsed = Lyrics.Parse(text);
        text = parsed.Lines.Count == 0
            ? ReplacePlainTextOffset(text, offsetMilliseconds)
            : Lyrics.Format(parsed with { Offset = TimeSpan.FromMilliseconds(offsetMilliseconds) });

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

    private static string ReplacePlainTextOffset(string text, int offsetMilliseconds)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Split('\n').Where(line => !System.Text.RegularExpressions.Regex.IsMatch(line.Trim(), @"^\[offset:[^\]]*\]$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)).ToList();
        if (offsetMilliseconds != 0) lines.Insert(0, $"[offset:{offsetMilliseconds}]");
        return string.Join(Environment.NewLine, lines);
    }
}
