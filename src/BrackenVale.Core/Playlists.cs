using System.Text;
using System.Text.RegularExpressions;

namespace BrackenVale.Core;

public static class Playlists
{
    private static readonly Regex ExtInf = new("^#EXTINF:-?\\d+,(.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> ReadM3u8(string playlistPath)
    {
        var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(playlistPath))!;
        var paths = new List<string>();
        foreach (var raw in File.ReadLines(playlistPath, Encoding.UTF8))
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            paths.Add(Path.GetFullPath(Path.IsPathRooted(line) ? line : Path.Combine(baseDirectory, line)));
        }
        return paths;
    }

    public static void WriteM3u8(string playlistPath, IEnumerable<string> trackPaths)
    {
        var fullPlaylistPath = Path.GetFullPath(playlistPath);
        var directory = Path.GetDirectoryName(fullPlaylistPath)!;
        Directory.CreateDirectory(directory);
        var temporary = fullPlaylistPath + ".tmp";
        try
        {
            using (var writer = new StreamWriter(temporary, false, new UTF8Encoding(false)))
            {
                writer.WriteLine("#EXTM3U");
                foreach (var trackPath in trackPaths)
                {
                    var fullTrackPath = Path.GetFullPath(trackPath);
                    var relative = Path.GetRelativePath(directory, fullTrackPath);
                    writer.WriteLine(Path.IsPathRooted(relative) ? fullTrackPath : relative);
                }
            }
            File.Move(temporary, fullPlaylistPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
