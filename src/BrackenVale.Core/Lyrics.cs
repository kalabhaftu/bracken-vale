using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BrackenVale.Core;

public static partial class Lyrics
{
    [GeneratedRegex(@"\[(\d{1,2}):(\d{2})(?:\.(\d{1,3}))?\]", RegexOptions.CultureInvariant)]
    private static partial Regex TimeStamp();
    [GeneratedRegex(@"^\[offset:([+-]?\d+)\]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OffsetLine();

    public static LyricsDocument Parse(string text)
    {
        var lines = new List<LyricsLine>();
        var offset = TimeSpan.Zero;
        foreach (var raw in text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var offsetMatch = OffsetLine().Match(raw.Trim());
            if (offsetMatch.Success && int.TryParse(offsetMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds))
            {
                offset = TimeSpan.FromMilliseconds(milliseconds);
                continue;
            }
            var timestamps = TimeStamp().Matches(raw);
            if (timestamps.Count == 0) continue;
            var lyric = raw[(timestamps[^1].Index + timestamps[^1].Length)..].Trim();
            foreach (Match timestamp in timestamps)
            {
                if (!int.TryParse(timestamp.Groups[1].Value, out var minutes) || !int.TryParse(timestamp.Groups[2].Value, out var seconds)) continue;
                var fraction = timestamp.Groups[3].Value;
                var millis = fraction.Length switch { 1 => int.Parse(fraction, CultureInfo.InvariantCulture) * 100, 2 => int.Parse(fraction, CultureInfo.InvariantCulture) * 10, 3 => int.Parse(fraction, CultureInfo.InvariantCulture), _ => 0 };
                lines.Add(new(TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(millis), lyric));
            }
        }
        return new(lines.OrderBy(line => line.Time).ToArray(), offset);
    }

    public static string Format(LyricsDocument document)
    {
        var output = new StringBuilder();
        if (document.Offset != TimeSpan.Zero) output.Append("[offset:").Append((long)document.Offset.TotalMilliseconds).AppendLine("]");
        foreach (var line in document.Lines)
        {
            var time = line.Time < TimeSpan.Zero ? TimeSpan.Zero : line.Time;
            output.Append('[').Append((int)time.TotalMinutes).Append(':').Append(time.Seconds.ToString("00", CultureInfo.InvariantCulture))
                .Append('.').Append((time.Milliseconds / 10).ToString("00", CultureInfo.InvariantCulture)).Append(']').AppendLine(line.Text);
        }
        return output.ToString();
    }
}
