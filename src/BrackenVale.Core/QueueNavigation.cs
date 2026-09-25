namespace BrackenVale.Core;

public static class QueueNavigation
{
    public static int NextIndex(int count, int currentIndex, bool automatic, string repeatMode)
    {
        if (count <= 0) return -1;
        if (automatic && string.Equals(repeatMode, "Track", StringComparison.OrdinalIgnoreCase)) return currentIndex >= 0 && currentIndex < count ? currentIndex : 0;
        var next = currentIndex < 0 ? 0 : currentIndex + 1;
        if (next < count) return next;
        return string.Equals(repeatMode, "Queue", StringComparison.OrdinalIgnoreCase) ? 0 : -1;
    }

    public static void ShuffleUpcoming<T>(IList<T> queue, int startIndex)
    {
        ArgumentNullException.ThrowIfNull(queue);
        if (startIndex < 0 || startIndex > queue.Count) throw new ArgumentOutOfRangeException(nameof(startIndex));
        for (var index = queue.Count - 1; index > startIndex; index--)
        {
            var next = Random.Shared.Next(startIndex, index + 1);
            (queue[index], queue[next]) = (queue[next], queue[index]);
        }
    }
}
