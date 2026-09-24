using System.Security.Cryptography;
using System.Text;
using TagLib;

namespace BrackenVale.Core;

public sealed class TagEditor(string backupDirectory)
{
    public TagBackup Save(string path, TagEdit edit)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(backupDirectory);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..10];
        var backup = Path.Combine(backupDirectory, $"{stamp}-{suffix}-{Path.GetFileName(path)}.bak");
        var staged = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileNameWithoutExtension(path)}.bracken-stage{Path.GetExtension(path)}");
        System.IO.File.Copy(path, backup, false);
        try
        {
            System.IO.File.Copy(path, staged, true);
            using (var media = TagLib.File.Create(staged))
            {
                var tag = media.Tag;
                if (edit.Title is not null) tag.Title = edit.Title;
                if (edit.Artist is not null) tag.Performers = Split(edit.Artist);
                if (edit.Album is not null) tag.Album = edit.Album;
                if (edit.AlbumArtist is not null) tag.AlbumArtists = Split(edit.AlbumArtist);
                if (edit.Genre is not null) tag.Genres = Split(edit.Genre);
                if (edit.Year.HasValue) tag.Year = edit.Year.Value;
                if (edit.TrackNumber.HasValue) tag.Track = edit.TrackNumber.Value;
                if (edit.Lyrics is not null) tag.Lyrics = edit.Lyrics;
                if (edit.ArtworkPath is not null) tag.Pictures = [new Picture(edit.ArtworkPath)];
                if (edit.CustomFields is { Count: > 0 })
                {
                    var xiph = media.GetTag(TagTypes.Xiph, true) as TagLib.Ogg.XiphComment
                        ?? throw new NotSupportedException("Custom Xiph comments are not supported by this file format.");
                    foreach (var field in edit.CustomFields) xiph.SetField(field.Key, [field.Value]);
                }
                media.Save();
            }
            // Same-directory replacement keeps the original intact until the fully written staged file is ready.
            System.IO.File.Move(staged, path, true);
            return new(path, backup, DateTime.UtcNow);
        }
        catch
        {
            if (System.IO.File.Exists(staged)) System.IO.File.Delete(staged);
            throw;
        }
    }

    public void Restore(TagBackup backup)
    {
        if (!System.IO.File.Exists(backup.BackupPath)) throw new FileNotFoundException("The saved tag backup is missing.", backup.BackupPath);
        var original = Path.GetFullPath(backup.OriginalPath);
        var restore = Path.Combine(Path.GetDirectoryName(original)!, $".{Path.GetFileNameWithoutExtension(original)}.bracken-restore{Path.GetExtension(original)}");
        try { System.IO.File.Copy(backup.BackupPath, restore, true); System.IO.File.Move(restore, original, true); }
        finally { if (System.IO.File.Exists(restore)) System.IO.File.Delete(restore); }
    }

    private static string[] Split(string value) => value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
