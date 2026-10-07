using System.Text.Json;
using Microsoft.UI.Xaml;

namespace MusicPlayer.App;

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
                    view = _libraryQueries.CurrentView, search = _libraryQueries.CurrentSearch,
                    group = _libraryQueries.CurrentGroup, playlist = _libraryQueries.CurrentPlaylist,
                    track = currentTrack is null ? null : _libraryQueries.TrackDto(currentTrack, includePath: true),
                    playing = _playback.IsPlaying, positionSeconds = _playback.Position / 1000d,
                    durationSeconds = _playback.Duration / 1000d, volume = _playback.Volume,
                    shuffle = _shuffle, repeat = _repeatMode,
                    repeatA = _repeatA?.TotalSeconds, repeatB = _repeatB?.TotalSeconds,
                    queue = QueueDtos(Math.Max(0, _queueIndex), 30), queueOffset = Math.Max(0, _queueIndex), queueTotal = _queue.Count, queueIndex = _queueIndex,
                    panel = _store.GetSetting("right-sidebar-mode") == "Info" ? "info" : "queue",
                    settings = settingState, resolvedTheme = ShellRoot.ActualTheme == ElementTheme.Light ? "Light" : "Dark", scan = ScanDto(),
                    updateCheckActive = Volatile.Read(ref _updateCheckActive) != 0,
                    updateAvailable = _webAvailableRelease is { } available ? new { tag = available.Tag, url = available.Url } : null
                };
            }
            case "getHome": return await Task.Run(() => _libraryQueries.HomeData(Int(payload, "pageSize", 8)));
            case "beginSearch": _libraryQueries.BeginSearch(Long(payload, "requestId")); return null;
            case "search": return await _libraryQueries.SearchAsync(payload);
            case "getTracks":
            {
                // Persist view state on the UI dispatcher, then perform disk hashing,
                // SQLite paging and DTO projection on a worker thread.
                var query = _libraryQueries.PrepareTrackPage(payload);
                return await Task.Run(() => _libraryQueries.TrackPage(query));
            }
            case "getGroups": return await Task.Run(() => _libraryQueries.GroupPage(payload));
            case "getFolders":
            {
                var scan = ScanDto();
                return await Task.Run(() => _libraryQueries.FolderData(payload, scan));
            }
            case "getArtistAlbums": return await Task.Run(() => _libraryQueries.ArtistAlbums(String(payload, "artist"), Int(payload, "offset"), Int(payload, "pageSize", 30)));
            case "getPlaylists": return await Task.Run(() => _libraryQueries.Playlists());
            case "getPlaylistTracks": return await Task.Run(() => PlaylistTrackPage(payload));
            case "getQueue": return QueuePage(payload);
            case "getCurrentTrack": return new
            {
                track = _playback.CurrentTrack is { } current ? _libraryQueries.TrackDto(current, true) : null,
                playing = _playback.IsPlaying, positionSeconds = Math.Max(0, _playback.Position) / 1000d,
                durationSeconds = Math.Max(0, _playback.Duration) / 1000d, volume = _playback.Volume,
                shuffle = _shuffle, repeat = _repeatMode, repeatA = _repeatA?.TotalSeconds, repeatB = _repeatB?.TotalSeconds,
                queueIndex = _queueIndex
            };
            case "getLyrics":
            {
                var track = ResolveRequestedLyricsTrack(payload);
                return await Task.Run(() => LyricsData(track));
            }
            case "getAudioSettings": return await AudioSettingsAsync();
            case "getSettings": return WebSettings();
            case "getDuplicates": return await DuplicatePageAsync(payload);
            case "getDuplicateFiles": return await Task.Run(() => DuplicateFiles(payload));
            case "getTrackDetails": return await TrackDetailsDataAsync(TrackFrom(payload, "id"));
            case "getTags":
            {
                var track = TrackFrom(payload, "id");
                return await Task.Run(() => TagsData(track));
            }
            case "getExclusions": return new { paths = _libraryLocations.GetScanExclusions() };
            case "setView": _libraryQueries.SetContext(payload); return null;
            default: throw new InvalidOperationException("This Music Player command is not available in the data handler.");
        }
    }

    private static long Long(JsonElement payload, string key) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(key, out var value) && value.TryGetInt64(out var result)
            ? result : 0;
}
