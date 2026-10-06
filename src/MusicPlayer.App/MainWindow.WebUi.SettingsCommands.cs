using System.Text.Json;

namespace BrackenVale.App;

public sealed partial class MainWindow
{
    private async Task<object?> HandleWebUiSettingsCommandAsync(string name, JsonElement payload)
    {
        switch (name)
        {
            case "setAudioDevice": await SetAudioDeviceAsync(String(payload, "id")); return null;
            case "setCrossfade": SetCrossfade(Int(payload, "seconds")); return null;
            case "refreshAudioDevices": return await AudioSettingsAsync();
            case "setEqualizerPreset": SetEqualizerPreset(String(payload, "name")); return null;
            case "setEqualizerBand": SetEqualizerBand(Int(payload, "index"), (float)Double(payload, "value")); return null;
            case "saveEqualizerPreset": SaveEqualizerPreset(String(payload, "name")); return null;
            case "updateSettings": await UpdateWebSettingsAsync(payload); return null;
            case "resetUiSettings":
            {
                var appearanceChanged = _playerSettings.ResetUiSettings(payload);
                if (appearanceChanged)
                {
                    _webArtworkAccent = null;
                    ApplyStoredAppearance();
                    ApplyTraySetting();
                    ResetAccent();
                    _webBridge?.SendEvent("artworkAccentChanged", new { color = (string?)null });
                }
                PublishSettingsChanged();
                PublishLibraryChanged();
                return WebSettings();
            }
            case "addExclusion": await AddExclusionAsync(); return null;
            case "removeExclusion": RemoveExclusion(String(payload, "path")); return null;
            case "checkUpdates": await CheckForUpdatesAsync(true); return null;
            default: throw new InvalidOperationException("This Music Player command is not available in the settings handler.");
        }
    }
}
