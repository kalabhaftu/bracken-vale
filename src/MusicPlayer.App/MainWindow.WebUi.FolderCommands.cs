using System.Text.Json;

namespace MusicPlayer.App;

public sealed partial class MainWindow
{
    private async Task<object?> HandleWebUiFolderCommandAsync(string name, JsonElement payload)
    {
        switch (name)
        {
            case "addFolder": await AddFolderFromWebAsync(); return null;
            case "removeRoot": await RemoveRootFromWebAsync(String(payload, "path")); return null;
            case "scanLibrary":
            case "rebuildLibraryIndex":
            {
                if (!_libraryScan.IsRunning)
                {
                    var roots = _libraryLocations.GetLibraryRoots();
                    if (roots.Length == 0 && name == "rebuildLibraryIndex") StartScan(roots, forceRefresh: true);
                    else if (roots.Length == 0) await AddFolderFromWebAsync();
                    else StartScan(roots, forceRefresh: name == "rebuildLibraryIndex");
                }
                else await ShowNoticeAsync("A library scan is already running.");
                return null;
            }
            case "toggleScanPause":
                if (_libraryScan.TogglePause()) PublishScanState();
                return null;
            case "cancelScan":
                if (_libraryScan.Cancel()) PublishScanState();
                return null;
            case "showInFolder":
            {
                var track = RequireTrack(payload, "id"); RevealFileInExplorer(track.Path); return null;
            }
            case "showFilePath": RevealFileInExplorer(String(payload, "path")); return null;
            default: throw new InvalidOperationException("This Music Player command is not available in the folder handler.");
        }
    }
}
