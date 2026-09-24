using System.Security.Cryptography;
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
        var artwork = SaveArtwork(tag.Pictures.FirstOrDefault(), artworkCache);
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

    private static string? SaveArtwork(IPicture? picture, string cache)
    {
        if (picture is null || picture.Data.Count == 0) return null;
        Directory.CreateDirectory(cache);
        var image = picture.Data.ToArray();
        var pathKey = Convert.ToHexString(SHA256.HashData(image));
        var extension = picture.MimeType switch { "image/png" => ".png", "image/jpeg" => ".jpg", "image/webp" => ".webp", _ => ".img" };
        var path = Path.Combine(cache, pathKey + extension);
        if (!System.IO.File.Exists(path))
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                System.IO.File.WriteAllBytes(temporary, image);
                try { System.IO.File.Move(temporary, path); }
                catch (IOException) when (System.IO.File.Exists(path)) { }
            }
            finally { if (System.IO.File.Exists(temporary)) System.IO.File.Delete(temporary); }
        }
        return path;
    }
}
