namespace BrackenVale.Core;

/// <summary>Resolves the product data folder and moves the in-development folder once.</summary>
public static class AppDataPaths
{
    private static readonly object MigrationGate = new();

    public static string Root
    {
        get
        {
            MigrateFromDevelopmentFolder();
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicPlayer");
        }
    }

    public static void MigrateFromDevelopmentFolder()
    {
        lock (MigrationGate)
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var oldPath = Path.Combine(local, "BrackenVale");
            var newPath = Path.Combine(local, "MusicPlayer");
            if (!Directory.Exists(oldPath)) return;

            try
            {
                if (!Directory.Exists(newPath))
                {
                    Directory.Move(oldPath, newPath);
                    return;
                }

                // A prior startup may have created the new folder for logs or settings
                // before migration. Bring over existing files only when no library DB
                // exists there yet; never overwrite a newer Music Player data set.
                if (!File.Exists(Path.Combine(newPath, "library.db")) && File.Exists(Path.Combine(oldPath, "library.db")))
                    CopyMissingFiles(oldPath, newPath);
            }
            catch (IOException) when (Directory.Exists(newPath))
            {
                if (!File.Exists(Path.Combine(newPath, "library.db")) && File.Exists(Path.Combine(oldPath, "library.db")))
                    CopyMissingFiles(oldPath, newPath);
            }
        }
    }

    private static void CopyMissingFiles(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            if (File.Exists(target)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
