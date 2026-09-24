using System.Text;

namespace BrackenVale.Core;

public sealed class LocalAppLog
{
    private const long MaxBytes = 4 * 1024 * 1024;
    private const int RetentionDays = 14;
    private readonly string _folder;
    private readonly object _gate = new();
    private bool _pruned;

    public static LocalAppLog Shared { get; } = new();
    public string FolderPath => _folder;

    public LocalAppLog(string? folder = null)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _folder = folder ?? Path.Combine(local, "BrackenVale", "Logs");
    }

    public void Info(string area, string message) => Write("INFO", area, message, null);
    public void Warning(string area, string message, Exception? exception = null) => Write("WARN", area, message, exception);
    public void Error(string area, string message, Exception exception) => Write("ERROR", area, message, exception);

    private void Write(string level, string area, string message, Exception? exception)
    {
        var entry = $"{DateTimeOffset.UtcNow:O} [{level}] [{area}] {message}" +
            (exception is null ? "" : Environment.NewLine + exception) + Environment.NewLine;
        if (Encoding.UTF8.GetByteCount(entry) > MaxBytes) entry = Truncate(entry);
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_folder);
                if (!_pruned)
                {
                    _pruned = true;
                    try
                    {
                        foreach (var file in Directory.EnumerateFiles(_folder, "bracken-vale-*.log*"))
                            if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-RetentionDays)) File.Delete(file);
                    }
                    catch { }
                }
                var path = Path.Combine(_folder, $"bracken-vale-{DateTime.UtcNow:yyyy-MM-dd}.log");
                if (File.Exists(path) && new FileInfo(path).Length + Encoding.UTF8.GetByteCount(entry) > MaxBytes)
                    File.Move(path, path + ".1", true);
                File.AppendAllText(path, entry, new UTF8Encoding(false));
            }
            catch
            {
                // Logging must never turn an ordinary app error into a second failure.
            }
        }
    }

    private static string Truncate(string value)
    {
        const string suffix = "\n[log entry truncated]\n";
        var limit = MaxBytes - Encoding.UTF8.GetByteCount(suffix);
        var low = 0; var high = value.Length;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (Encoding.UTF8.GetByteCount(value.AsSpan(0, middle)) <= limit) low = middle;
            else high = middle - 1;
        }
        return value[..low] + suffix;
    }
}
