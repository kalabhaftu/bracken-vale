using System.Text.Json;
using BrackenVale.Core;
using Windows.ApplicationModel.DataTransfer;

namespace BrackenVale.App;

public sealed partial class MainWindow
{
    private async Task<object?> HandleWebUiUtilityCommandAsync(string name, JsonElement payload)
    {
        switch (name)
        {
            case "reportFrontendError":
            {
                var kind = String(payload, "kind");
                if (kind is "script" or "rejection")
                    LocalAppLog.Shared.Warning("web-ui", $"The local interface reported an unhandled {kind} error.");
                return null;
            }
            case "openLogs": OpenLogsFolder_Click(this, new Microsoft.UI.Xaml.RoutedEventArgs()); return null;
            case "exportLogs": await ExportLogsAsync(); return null;
            case "openRelease": OpenRelease(String(payload, "url")); return null;
            case "setPanelMode":
                _store.SetSetting("right-sidebar-mode", String(payload, "mode") == "info" ? "Info" : "Queue"); return null;
            case "openDefaultApps":
                if (!await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:defaultapps"))) throw new InvalidOperationException("Windows Settings could not be opened.");
                return null;
            case "copyTrackPath":
            {
                var path = _libraryQueries.ResolveTrackPath(String(payload, "id"));
                if (string.IsNullOrWhiteSpace(path)) throw new KeyNotFoundException("The track location is no longer available.");
                var package = new DataPackage();
                package.SetText(path);
                Clipboard.SetContent(package);
                return null;
            }
            default: throw new InvalidOperationException("This Music Player command is not available in the utility handler.");
        }
    }
}
