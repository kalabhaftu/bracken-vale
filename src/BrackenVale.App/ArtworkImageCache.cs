using Microsoft.UI.Xaml.Media.Imaging;

namespace BrackenVale.App;

/// <summary>Shares decoded artwork thumbnails between virtualized rows under a fixed decoded-pixel budget.</summary>
internal sealed class ArtworkImageCache(long maximumBytes = 64L * 1024 * 1024)
{
    private sealed record Entry(string Key, BitmapImage Image, long EstimatedBytes);

    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _entries = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly LinkedList<Entry> _recent = [];
    private long _currentBytes;

    public BitmapImage? Get(string? path, int decodeSize)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        decodeSize = Math.Clamp(decodeSize, 48, 1024);
        var fullPath = Path.GetFullPath(path);
        var key = fullPath + "\0" + decodeSize.ToString(System.Globalization.CultureInfo.InvariantCulture);

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var cached))
            {
                _recent.Remove(cached);
                _recent.AddFirst(cached);
                return cached.Value.Image;
            }
        }

        var image = new BitmapImage
        {
            DecodePixelWidth = decodeSize,
            DecodePixelHeight = decodeSize,
            UriSource = new Uri(fullPath)
        };
        var bytes = checked((long)decodeSize * decodeSize * 4);

        lock (_gate)
        {
            // Another virtualized row may have requested this image while it was decoding.
            if (_entries.TryGetValue(key, out var existing))
            {
                _recent.Remove(existing);
                _recent.AddFirst(existing);
                return existing.Value.Image;
            }

            if (bytes > maximumBytes) return image;
            var node = _recent.AddFirst(new Entry(key, image, bytes));
            _entries.Add(key, node);
            _currentBytes += bytes;
            while (_currentBytes > maximumBytes && _recent.Last is { } last)
            {
                _recent.RemoveLast();
                _entries.Remove(last.Value.Key);
                _currentBytes -= last.Value.EstimatedBytes;
            }
        }

        return image;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _recent.Clear();
            _currentBytes = 0;
        }
    }
}
