namespace BrackenVale.Core;

public sealed class ScanControl : IDisposable
{
    private readonly object _gate = new();
    private TaskCompletionSource _resumed = Completed();
    private readonly CancellationTokenSource _cancel = new();
    public CancellationToken Token => _cancel.Token;
    public void Pause() { lock (_gate) _resumed = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    public void Resume() { lock (_gate) _resumed.TrySetResult(); }
    public void Cancel() => _cancel.Cancel();
    internal async ValueTask WaitIfPausedAsync()
    {
        Task wait;
        lock (_gate) wait = _resumed.Task;
        await wait.WaitAsync(Token).ConfigureAwait(false);
    }
    public void Dispose() => _cancel.Dispose();
    private static TaskCompletionSource Completed() { var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); t.SetResult(); return t; }
}

public sealed class LibraryScanner(LocalAppLog? log = null)
{
    private readonly LocalAppLog _log = log ?? LocalAppLog.Shared;
    public static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".wave", ".aif", ".aiff", ".m4a", ".m4b", ".mp4", ".aac", ".ogg", ".oga", ".opus", ".wma", ".ape", ".wv", ".tta", ".mpc", ".dsf", ".dff"
    };

    private static readonly HashSet<string> SystemFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "WindowsApps", "Recovery", "System Volume Information", "$Recycle.Bin", "PerfLogs"
    };

    public static IEnumerable<string> DefaultRoots()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            var include = false;
            try { include = drive.DriveType == DriveType.Fixed && drive.IsReady; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (include) yield return drive.RootDirectory.FullName;
        }
    }

    public async Task ScanAsync(
        IEnumerable<string> roots,
        IEnumerable<string> ignoredDirectories,
        ScanControl control,
        Func<string, CancellationToken, ValueTask> onAudioFile,
        IProgress<ScanProgress>? progress = null,
        Action<string>? directoryVisited = null)
    {
        var ignored = ignoredDirectories.Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))).ToHashSet(PathComparer);
        var pending = new Stack<string>(roots.Reverse().Select(Normalize));
        var filesFound = 0;
        var directoriesVisited = 0;
        while (pending.TryPop(out var directory))
        {
            control.Token.ThrowIfCancellationRequested();
            await control.WaitIfPausedAsync().ConfigureAwait(false);
            if (ignored.Contains(directory) || IsSystemDirectory(directory) || IsReparsePoint(directory)) continue;
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { _log.Warning("scanner", $"Could not enumerate directory '{directory}'.", ex); continue; }
            directoriesVisited++;
            directoryVisited?.Invoke(directory);
            foreach (var entry in entries)
            {
                control.Token.ThrowIfCancellationRequested();
                await control.WaitIfPausedAsync().ConfigureAwait(false);
                if (IsReparsePoint(entry)) continue;
                if (Directory.Exists(entry))
                {
                    var normalized = Normalize(entry);
                    if (!ignored.Contains(normalized) && !IsSystemDirectory(normalized)) pending.Push(normalized);
                }
                else if (AudioExtensions.Contains(Path.GetExtension(entry)))
                {
                    await onAudioFile(entry, control.Token).ConfigureAwait(false);
                    filesFound++;
                    if ((filesFound & 63) == 0) progress?.Report(new(filesFound, directoriesVisited, entry));
                }
            }
        }
        progress?.Report(new(filesFound, directoriesVisited, string.Empty));
    }

    private static bool IsSystemDirectory(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (name.Equals("AppData", StringComparison.OrdinalIgnoreCase)) return true;
        if (!SystemFolders.Contains(name)) return false;
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
        var root = Path.GetPathRoot(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return parent is not null && root is not null && string.Equals(
            Path.TrimEndingDirectorySeparator(parent), Path.TrimEndingDirectorySeparator(root), comparison);
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
