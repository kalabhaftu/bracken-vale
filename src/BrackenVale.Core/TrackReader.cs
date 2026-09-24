using System.Security.Cryptography;
using System.Text;
using TagLib;

namespace BrackenVale.Core;

public static class TrackReader
{
    public static Track Read(string path, string artworkCache)
    {
        path = Path.GetFullPath(path);
        var info = new FileInfo(path);
        using var media = TagLib.File.Create(path);
        var tag = media.Tag;
        var artwork = SaveArtwork(tag.Pictures.FirstOrDefault(), path, artworkCache);
        return new(
            path,
            string.IsNullOrWhiteSpace(tag.Title) ? Path.GetFileNameWithoutExtension(path) : tag.Title,
            string.Join("; ", tag.Performers),
            tag.Album ?? string.Empty,
            string.Join("; ", tag.AlbumArtists),
            string.Join("; ", tag.Genres),
            tag.Year,
            tag.Track,
            media.Properties.Duration,
            info.Length,
            info.LastWriteTimeUtc,
            DateTime.UtcNow,
            ArtworkPath: artwork);
    }

    private static string? SaveArtwork(IPicture? picture, string trackPath, string cache)
    {
        if (picture is null || picture.Data.Count == 0) return null;
        Directory.CreateDirectory(cache);
        var pathKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(trackPath)));
        var extension = picture.MimeType switch { "image/png" => ".png", "image/jpeg" => ".jpg", "image/webp" => ".webp", _ => ".img" };
        var path = Path.Combine(cache, pathKey + extension);
        if (!System.IO.File.Exists(path))
        {
            var temporary = path + ".tmp";
            System.IO.File.WriteAllBytes(temporary, picture.Data.ToArray());
            System.IO.File.Move(temporary, path, true);
        }
        return path;
    }
}
