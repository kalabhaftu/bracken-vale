using System.Text.Json;
using BrackenVale.Core;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace BrackenVale.App;

/// <summary>Owns the WebView2 boundary and only forwards named, explicitly allowed UI commands.</summary>
internal sealed class WebViewBridge(WebView2 view, WebUiCommandRouter commandRouter)
{
    private const string UiHost = "musicplayer.local";
    private const string ArtworkHost = "artwork.musicplayer.local";
    private const string AssetsHost = "assets.musicplayer.local";
    private CoreWebView2? _core;

    public bool IsReady => _core is not null;

    public async Task InitializeAsync(string contentFolder, string artworkFolder, string assetsFolder, string userDataFolder)
    {
        if (!Directory.Exists(contentFolder)) throw new DirectoryNotFoundException($"The packaged UI folder is missing: {contentFolder}");
        var fullUserDataFolder = Path.GetFullPath(userDataFolder);
        Directory.CreateDirectory(fullUserDataFolder);
        var environment = await CoreWebView2Environment.CreateWithOptionsAsync(
            null, fullUserDataFolder, new CoreWebView2EnvironmentOptions());
        Exception? initializationFailure = null;
        void CaptureInitializationFailure(WebView2 _, CoreWebView2InitializedEventArgs args) => initializationFailure = args.Exception;
        view.CoreWebView2Initialized += CaptureInitializationFailure;
        try { await view.EnsureCoreWebView2Async(environment); }
        finally { view.CoreWebView2Initialized -= CaptureInitializationFailure; }
        _core = view.CoreWebView2 ?? throw new InvalidOperationException(
            "WebView2 initialization completed without a CoreWebView2 instance.", initializationFailure);
        _core.Settings.AreDefaultContextMenusEnabled = false;
        _core.Settings.AreDefaultScriptDialogsEnabled = false;
        _core.Settings.IsStatusBarEnabled = false;
        _core.Settings.IsZoomControlEnabled = false;
        _core.Settings.AreDevToolsEnabled = false;
        _core.SetVirtualHostNameToFolderMapping(UiHost, Path.GetFullPath(contentFolder), CoreWebView2HostResourceAccessKind.DenyCors);
        if (Directory.Exists(artworkFolder))
            _core.SetVirtualHostNameToFolderMapping(ArtworkHost, Path.GetFullPath(artworkFolder), CoreWebView2HostResourceAccessKind.DenyCors);
        if (Directory.Exists(assetsFolder))
            _core.SetVirtualHostNameToFolderMapping(AssetsHost, Path.GetFullPath(assetsFolder), CoreWebView2HostResourceAccessKind.DenyCors);
        _core.NavigationStarting += NavigationStarting;
        _core.NavigationCompleted += NavigationCompleted;
        _core.NewWindowRequested += (_, args) => args.Handled = true;
        _core.WebMessageReceived += MessageReceived;
        _core.Navigate($"https://{UiHost}/index.html");
    }

    public void SendEvent(string name, object? data)
    {
        if (_core is null) return;
        try { _core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "event", name, data })); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
    }

    private static void NavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            (uri.Host != UiHost && uri.Host != ArtworkHost && uri.Host != AssetsHost)) args.Cancel = true;
    }

    private static void NavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (args.IsSuccess)
            LocalAppLog.Shared.Info("web-ui", $"Loaded local Music Player interface ({sender.DocumentTitle}).");
        else
            LocalAppLog.Shared.Warning("web-ui", $"The local Music Player interface failed to load ({args.WebErrorStatus}).");
    }

    private async void MessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        string? id = null;
        string? safeCommandName = null;
        try
        {
            if (!Uri.TryCreate(args.Source, UriKind.Absolute, out var origin) || origin.Scheme != Uri.UriSchemeHttps || origin.Host != UiHost) return;
            using var document = JsonDocument.Parse(args.WebMessageAsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.GetString() != "command" ||
                !root.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("name", out var nameValue) || nameValue.ValueKind != JsonValueKind.String) return;
            id = idValue.GetString();
            var name = nameValue.GetString();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || !commandRouter.CanRoute(name))
                throw new InvalidOperationException("This UI command is not available.");
            safeCommandName = name;
            var payload = root.TryGetProperty("payload", out var value) ? value.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
            var result = await commandRouter.RouteAsync(name, payload);
            Respond(id, true, result, null);
        }
        catch (Exception ex)
        {
            if (safeCommandName is not null)
                LocalAppLog.Shared.Warning("web-ui-command", $"The '{safeCommandName}' command failed ({ex.GetType().Name}).");
            if (id is not null) Respond(id, false, null, ex.Message);
        }
    }

    private void Respond(string id, bool ok, object? data, string? error)
    {
        if (_core is null) return;
        try { _core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "result", id, ok, data, error })); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
    }
}
