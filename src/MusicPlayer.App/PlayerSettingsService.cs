using System.Text.Json;
using BrackenVale.Core;

namespace BrackenVale.App;

/// <summary>
/// Projects and persists the settings exposed by the local player UI without depending on WinUI.
/// </summary>
internal sealed class PlayerSettingsService(LibraryStore store)
{
    private static readonly Dictionary<string, string> WebSettingKeys = new(StringComparer.Ordinal)
    {
        ["theme"] = "theme", ["accentMode"] = "accent-mode", ["accentManual"] = "accent-manual", ["accentColor"] = "accent-color",
        ["windowMaterial"] = "window-material", ["motionStyle"] = "motion-style", ["hideDuplicates"] = "hide-exact-duplicates",
        ["autoOpenPanel"] = "auto-open-side-panel", ["showArtwork"] = "show-list-artwork", ["showArtist"] = "show-list-artist",
        ["showAlbum"] = "show-list-album", ["showAdded"] = "show-list-added", ["showYear"] = "show-list-year", ["showDuration"] = "show-list-duration",
        ["showFavorite"] = "show-list-favorite", ["checkUpdates"] = "check-updates", ["minimizeToTray"] = "minimize-to-tray",
        ["navigationWidth"] = "navigation-pane-width", ["browseWidth"] = "browse-pane-width", ["navigationOrder"] = "navigation-order",
        ["hiddenPanels"] = "hidden-panels", ["rightSidebarMode"] = "right-sidebar-mode"
    };

    public object ProjectWebSettings(string resolvedTheme, string? artworkAccent)
    {
        var stored = store.GetSettings("");
        string Value(string key, string fallback) => stored.TryGetValue(key, out var value) ? value : fallback;
        bool On(string key, bool fallback = false) => stored.TryGetValue(key, out var value) ? value == "true" : fallback;
        return new
        {
            theme = Value("theme", "System"), accentMode = Value("accent-mode", "Native"), accentManual = On("accent-manual"),
            resolvedTheme, accentColor = Value("accent-color", "#b7ff2d"), artworkAccent,
            windowMaterial = Value("window-material", "Acrylic"), motionStyle = Value("motion-style", "Subtle"),
            hideDuplicates = Value("hide-exact-duplicates", "true") != "false", autoOpenPanel = Value("auto-open-side-panel", "true") != "false",
            showArtwork = Value("show-list-artwork", "true") != "false", showArtist = Value("show-list-artist", "true") != "false",
            showAlbum = Value("show-list-album", "true") != "false", showAdded = Value("show-list-added", "true") != "false",
            showYear = On("show-list-year"), showDuration = Value("show-list-duration", "true") != "false",
            showFavorite = Value("show-list-favorite", "true") != "false", checkUpdates = Value("check-updates", "true") != "false",
            minimizeToTray = On("minimize-to-tray"), navigationWidth = Value("navigation-pane-width", "232"), browseWidth = Value("browse-pane-width", "280"),
            navigationOrder = ReadStringArray("navigation-order"), hiddenPanels = ReadStringArray("hidden-panels"),
            rightSidebarMode = Value("right-sidebar-mode", "Queue")
        };
    }

    /// <returns>Whether an accent setting was included, so the shell can refresh its visual accent.</returns>
    public bool PersistWebSettings(JsonElement payload)
    {
        if (!payload.TryGetProperty("settings", out var settings) || settings.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Settings are missing.");

        foreach (var item in settings.EnumerateObject())
        {
            if (!WebSettingKeys.TryGetValue(item.Name, out var key))
                throw new ArgumentException($"The setting '{item.Name}' is not editable from the player.");
            var value = item.Value.ValueKind switch
            {
                JsonValueKind.True => "true", JsonValueKind.False => "false", JsonValueKind.String => item.Value.GetString() ?? "",
                JsonValueKind.Array => item.Value.GetRawText(), JsonValueKind.Number => item.Value.ToString(),
                _ => throw new ArgumentException($"The value for '{item.Name}' is invalid.")
            };
            ValidateSetting(item.Name, value);
            store.SetSetting(key, value);
        }

        return settings.EnumerateObject().Any(item => item.Name is "accentMode" or "accentManual" or "accentColor");
    }

    private string[] ReadStringArray(string key)
    {
        try { return store.GetSetting(key) is { } json ? JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>() : Array.Empty<string>(); }
        catch (JsonException ex)
        {
            LocalAppLog.Shared.Warning("settings", $"Saved setting '{key}' was invalid JSON.", ex);
            return Array.Empty<string>();
        }
    }

    private static void ValidateSetting(string key, string value)
    {
        var valid = key switch
        {
            "theme" => value is "System" or "Light" or "Dark",
            "accentMode" => value is "Native" or "Artwork",
            "accentManual" or "hideDuplicates" or "autoOpenPanel" or "showArtwork" or "showArtist" or "showAlbum" or "showAdded" or "showYear" or "showDuration" or "showFavorite" or "checkUpdates" or "minimizeToTray" => value is "true" or "false",
            "windowMaterial" => value is "Mica" or "Acrylic" or "Opaque",
            "motionStyle" => value is "Off" or "Subtle" or "Expressive",
            "accentColor" => System.Text.RegularExpressions.Regex.IsMatch(value, "^#[0-9a-fA-F]{6}$"),
            "navigationWidth" => int.TryParse(value, out var nav) && nav is >= 180 and <= 360,
            "browseWidth" => int.TryParse(value, out var browse) && browse is >= 180 and <= 480,
            "rightSidebarMode" => value is "Queue" or "Info",
            "navigationOrder" or "hiddenPanels" => JsonLooksStringArray(value),
            _ => false
        };
        if (!valid) throw new ArgumentException($"The value for '{key}' is not valid.");
    }

    private static bool JsonLooksStringArray(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind == JsonValueKind.Array &&
                   document.RootElement.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String);
        }
        catch (JsonException) { return false; }
    }
}
