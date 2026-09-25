using System.Text;
using System.Text.Json.Serialization;
using System.Text.Json;
using TagLib;

namespace BrackenVale.Core;

public sealed record LyricsSearchResult(
    [property: JsonPropertyName("trackName")] string TrackName,
    [property: JsonPropertyName("artistName")] string ArtistName,
    [property: JsonPropertyName("albumName")] string? AlbumName,
    [property: JsonPropertyName("syncedLyrics")] string? SyncedLyrics,
    [property: JsonPropertyName("plainLyrics")] string? PlainLyrics);

public static class LyricsFiles
{
    public static string SidecarPath(string trackPath) => Path.ChangeExtension(trackPath, ".lrc");

    public static string ReadRaw(string trackPath)
    {
        var sidecar = SidecarPath(trackPath);
        try { if (System.IO.File.Exists(sidecar)) return System.IO.File.ReadAllText(sidecar, Encoding.UTF8); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { LocalAppLog.Shared.Warning("lyrics-reader", $"Could not read lyrics sidecar '{sidecar}'.", ex); return string.Empty; }
        try
        {
            using var media = TagLib.File.Create(trackPath);
            return media.Tag.Lyrics ?? string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TagLib.CorruptFileException or TagLib.UnsupportedFormatException or NotSupportedException)
        { LocalAppLog.Shared.Warning("lyrics-reader", $"Could not read embedded lyrics in '{trackPath}'.", ex); return string.Empty; }
    }

    public static void SaveSidecar(string trackPath, string lyrics)
    {
        var destination = SidecarPath(trackPath);
        var temporary = destination + ".tmp";
        try
        {
            System.IO.File.WriteAllText(temporary, lyrics, new UTF8Encoding(false));
            System.IO.File.Move(temporary, destination, true);
        }
        finally { if (System.IO.File.Exists(temporary)) System.IO.File.Delete(temporary); }
    }

    public static async Task<IReadOnlyList<LyricsSearchResult>> SearchLrclibAsync(string title, string artist, CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BrackenVale/0.1 (+https://github.com/kalabhaftu/bracken-vale)");
        var url = "https://lrclib.net/api/search?track_name=" + Uri.EscapeDataString(title) + "&artist_name=" + Uri.EscapeDataString(artist);
        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<List<LyricsSearchResult>>(body, cancellationToken: cancellationToken).ConfigureAwait(false) ?? [];
    }
}

public sealed record TrackDetails(string Path, string Container, TimeSpan Duration, int BitrateKbps, int SampleRateHz, int BitsPerSample, long FileSize, DateTime ModifiedUtc);

public static class TrackInformation
{
    public static TrackDetails Read(string path)
    {
        var info = new FileInfo(path);
        using var media = TagLib.File.Create(path);
        return new(Path.GetFullPath(path), media.MimeType, media.Properties.Duration, media.Properties.AudioBitrate,
            media.Properties.AudioSampleRate, media.Properties.BitsPerSample, info.Length, info.LastWriteTimeUtc);
    }
}
