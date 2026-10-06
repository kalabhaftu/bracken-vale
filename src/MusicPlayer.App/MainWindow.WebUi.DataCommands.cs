using System.Text.Json;
using Microsoft.UI.Xaml;

namespace BrackenVale.App;

public sealed partial class MainWindow
{
    private async Task<object?> HandleWebUiDataCommandAsync(string name, JsonElement payload)
    {
        switch (name)
        {
            case "getBootstrap":
            {
                _webUiBootstrapped = true;
                var currentTrack = _playback.CurrentTrack;
                var settingState = WebSettings();
                return new
                {
                    view = _libraryQueries.CurrentView, track = currentTrack is null ? null : _libraryQueries.TrackDto(currentTrack, includePath: true),
                    playing = _playback.IsPlaying, positionSeconds = _playback.Position / 1000d,
                    durationSeconds = _playback.Duration / 1000d, volume = _playback.Volume,
                    shuffle = _shuffle, repeat = _repeatMode,
                    repeatA = _repeatA?.TotalSeconds, repeatB = _repeatB?.TotalSeconds,
                    queue = QueueDtos(Math.Max(0, _queueIndex), 30), queueOffset = Math.Max(0, _queueIndex), queueTotal = _queue.Count, queueIndex = _queueIndex,
                    panel = _store.GetSetting("right-sidebar-mode") == "Info" ? "info" : "queue",
                    settings = settingState, resolvedTheme = ShellRoot.ActualTheme == ElementTheme.Light ? "Light" : "Dark", scan = ScanDto(),
                    updateAvailable = _webAvailableRelease is { } available ? new { tag = available.Tag, url = available.Url } : null
                };
            }
            case "getHome": return _libraryQueries.HomeData(Int(payload, "pageSize", 8));
            case "search": return _libraryQueries.Search(payload);
            case "getTracks": return _libraryQueries.TrackPage(payload);
            case "getGroups": return _libraryQueries.GroupPage(payload);
            case "getFolders": return _libraryQueries.FolderData(payload, ScanDto());
            case "getArtistAlbums": return _libraryQueries.ArtistAlbums(String(payload, "artist"), Int(payload, "offset"), Int(payload, "pageSize", 30));
            case "getPlaylists": return _libraryQueries.Playlists();
            case "getPlaylistTracks": return PlaylistTrackPage(payload);
            case "getQueue": return QueuePage(payload);
            case "getCurrentTrack": return new
            {
                track = _playback.CurrentTrack is { } current ? _libraryQueries.TrackDto(current, true) : null,
                playing = _playback.IsPlaying, positionSeconds = Math.Max(0, _playback.Position) / 1000d,
                durationSeconds = Math.Max(0, _playback.Duration) / 1000d, volume = _playback.Volume,
                shuffle = _shuffle, repeat = _repeatMode, repeatA = _repeatA?.TotalSeconds, repeatB = _repeatB?.TotalSeconds
            };
            case "getLyrics": return LyricsData(TrackFrom(payload, "id") ?? _playback.CurrentTrack);
            case "getAudioSettings": return await AudioSettingsAsync();
            case "getSettings": return WebSettings();
            case "getDuplicates": return await DuplicatePageAsync(payload);
            case "getDuplicateFiles": return DuplicateFiles(payload);
            case "getTrackDetails": return await TrackDetailsDataAsync(TrackFrom(payload, "id"));
            case "getTags": return TagsData(TrackFrom(payload, "id"));
            case "getExclusions": return new { paths = _libraryLocations.GetScanExclusions() };
            case "setView": _libraryQueries.SetContext(payload); return null;
            default: throw new InvalidOperationException("This Music Player command is not available in the data handler.");
        }
    }
}
