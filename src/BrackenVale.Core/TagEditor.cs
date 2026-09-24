using System.Security.Cryptography;
using System.Text;
using TagLib;

namespace BrackenVale.Core;

public sealed class TagEditor(string backupDirectory)
{
    private static readonly HashSet<string> CommonFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "TITLE", "ARTIST", "ALBUM", "ALBUMARTIST", "ALBUM ARTIST", "GENRE", "DATE", "YEAR", "TRACK", "TRACKNUMBER", "LYRICS",
        "COVERART", "METADATA_BLOCK_PICTURE", "WM/Title", "WM/Author", "WM/AlbumTitle", "WM/AlbumArtist", "WM/Genre", "WM/Year",
        "WM/TrackNumber", "WM/Lyrics", "WM/Picture", "Album Artist", "Track"
    };
    private static readonly HashSet<string> CommonId3Frames = new(StringComparer.OrdinalIgnoreCase)
        { "TIT2", "TPE1", "TALB", "TPE2", "TCON", "TYER", "TDRC", "TRCK" };

    public static string? CustomFieldFormat(string path) => FormatFor(path) switch
    {
        CustomFormat.Xiph => "Xiph/Vorbis comments",
        CustomFormat.Id3v2 => "ID3v2 text frames and user text",
        CustomFormat.Asf => "ASF descriptors",
        CustomFormat.Ape => "APEv2 items",
        _ => null
    };

    public static IReadOnlyDictionary<string, string> ReadCustomFields(string path)
    {
        using var media = TagLib.File.Create(path);
        return ReadCustomFields(media, path);
    }

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
                ApplyCustomFields(media, path, edit.CustomFields);
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

    private static void ApplyCustomFields(TagLib.File media, string path, IReadOnlyDictionary<string, string>? fields)
    {
        if (fields is null) return;
        var format = FormatFor(path);
        if (format == CustomFormat.Unsupported) throw new NotSupportedException("Custom tags are not supported for this file format.");
        var desired = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rawKey, value) in fields)
        {
            var key = rawKey.Trim();
            if (key.Length == 0 || key.Contains('=') || key.Contains('\n') || key.Contains('\r')) throw new ArgumentException("Custom tag names cannot be empty or contain '=', or line breaks.");
            if (format == CustomFormat.Xiph && key.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
                throw new ArgumentException("Xiph field names can contain only ASCII letters, digits, and underscores.");
            if (CommonFields.Contains(key)) throw new ArgumentException($"'{key}' is already edited in a standard tag field.");
            desired[key] = value;
        }

        var existing = ReadCustomFields(media, path);
        foreach (var key in existing.Keys.Where(key => !desired.ContainsKey(key))) SetCustomField(media, format, key, string.Empty);
        foreach (var (key, value) in desired)
        {
            var storedKey = existing.Keys.FirstOrDefault(existingKey => existingKey.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? key;
            SetCustomField(media, format, storedKey, value);
        }
    }

    private static IReadOnlyDictionary<string, string> ReadCustomFields(TagLib.File media, string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        switch (FormatFor(path))
        {
            case CustomFormat.Xiph when media.GetTag(TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph:
                foreach (var key in xiph.Where(key => !CommonFields.Contains(key)))
                    result[key] = string.Join("; ", xiph.GetField(key));
                break;
            case CustomFormat.Id3v2 when media.GetTag(TagTypes.Id3v2) is TagLib.Id3v2.Tag id3:
                foreach (var frame in id3.GetFrames<TagLib.Id3v2.TextInformationFrame>())
                {
                    if (frame is TagLib.Id3v2.UserTextInformationFrame user)
                    {
                        if (!string.IsNullOrWhiteSpace(user.Description) && user.Text.Length > 0)
                            result[user.Description] = string.Join("; ", user.Text);
                        continue;
                    }
                    var frameId = Encoding.ASCII.GetString(frame.FrameId.Data);
                    if (!CommonId3Frames.Contains(frameId) && frame.Text.Length > 0)
                        result["ID3:" + frameId] = string.Join("; ", frame.Text);
                }
                break;
            case CustomFormat.Asf when media.GetTag(TagTypes.Asf) is TagLib.Asf.Tag asf:
                foreach (var descriptor in asf.Where(descriptor => !CommonFields.Contains(descriptor.Name) && descriptor.Type == TagLib.Asf.DataType.Unicode))
                    result[descriptor.Name] = descriptor.ToString();
                break;
            case CustomFormat.Ape when media.GetTag(TagTypes.Ape) is TagLib.Ape.Tag ape:
                foreach (var key in ape.Where(key => !CommonFields.Contains(key)))
                    if (ape.GetItem(key) is { Type: TagLib.Ape.ItemType.Text } item)
                        result[key] = string.Join("; ", item.ToStringArray());
                break;
        }
        return result;
    }

    private static void SetCustomField(TagLib.File media, CustomFormat format, string key, string value)
    {
        var values = Split(value);
        switch (format)
        {
            case CustomFormat.Xiph:
                var xiph = media.GetTag(TagTypes.Xiph, true) as TagLib.Ogg.XiphComment
                    ?? throw new NotSupportedException("Xiph comments are not supported by this file format.");
                xiph.SetField(key, values);
                break;
            case CustomFormat.Id3v2:
                var id3 = media.GetTag(TagTypes.Id3v2, true) as TagLib.Id3v2.Tag
                    ?? throw new NotSupportedException("ID3v2 tags are not supported by this file format.");
                if (key.StartsWith("ID3:", StringComparison.OrdinalIgnoreCase))
                {
                    var frameId = key[4..].ToUpperInvariant();
                    if (frameId.Length != 4 || frameId.Any(character => !char.IsAsciiLetterOrDigit(character)) || CommonId3Frames.Contains(frameId) || frameId == "TXXX")
                        throw new ArgumentException("Use ID3: followed by a non-standard four-character text frame identifier.");
                    id3.SetTextFrame(ByteVector.FromString(frameId, StringType.Latin1), values);
                    break;
                }
                var frame = TagLib.Id3v2.UserTextInformationFrame.Get(id3, key, false);
                if (values.Length == 0) { if (frame is not null) id3.RemoveFrame(frame); }
                else (frame ?? TagLib.Id3v2.UserTextInformationFrame.Get(id3, key, true)!).Text = values;
                break;
            case CustomFormat.Asf:
                var asf = media.GetTag(TagTypes.Asf, true) as TagLib.Asf.Tag
                    ?? throw new NotSupportedException("ASF tags are not supported by this file format.");
                asf.SetDescriptorStrings(values, key);
                break;
            case CustomFormat.Ape:
                var ape = media.GetTag(TagTypes.Ape, true) as TagLib.Ape.Tag
                    ?? throw new NotSupportedException("APEv2 tags are not supported by this file format.");
                ape.SetValue(key, values);
                break;
            default:
                throw new NotSupportedException("Custom tags are not supported for this file format.");
        }
    }

    private static CustomFormat FormatFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".flac" or ".ogg" or ".oga" or ".opus" => CustomFormat.Xiph,
        ".wma" => CustomFormat.Asf,
        ".ape" or ".wv" or ".mpc" => CustomFormat.Ape,
        ".mp3" or ".wav" or ".wave" or ".aif" or ".aiff" or ".aac" or ".tta" or ".dsf" or ".dff" => CustomFormat.Id3v2,
        _ => CustomFormat.Unsupported
    };

    private enum CustomFormat { Unsupported, Xiph, Id3v2, Asf, Ape }
}
