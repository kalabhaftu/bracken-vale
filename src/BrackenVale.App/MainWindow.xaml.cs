using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using BrackenVale.Core;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI;
using WinRT.Interop;

namespace BrackenVale.App;

public sealed partial class MainWindow : Window
{
    private readonly LibraryStore _store = LibraryStore.InAppData();
    private readonly string _appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrackenVale");
    private readonly ObservableCollection<Track> _tracks = [];
    private readonly ObservableCollection<Playlist> _playlists = [];
    private readonly List<Track> _queue = [];
    private readonly PlaybackService _playback = new();
    private SystemMediaTransportControls? _systemControls;
    private TrayIconService? _tray;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private ScanControl? _scanControl;
    private TrackSort _sort = TrackSort.Title;
    private bool _descending;
    private bool _shuffle;
    private bool _updatingPosition;
    private bool _countedCurrentPlay;
    private long _heardMilliseconds;
    private long _lastPlayCountPosition;
    private bool _crossfadeInProgress;
    private string? _crossfadeFailureSource;
    private int _queueIndex = -1;
    private string _view = "Songs";
    private string _repeatMode = "Off";
    private Playlist? _selectedPlaylist;
    private TimeSpan? _repeatA;
    private TimeSpan? _repeatB;
    private DateTime _lastSessionSave = DateTime.UtcNow;
    private LyricsDocument _currentLyrics = new([], TimeSpan.Zero);

    public MainWindow()
    {
        InitializeComponent();
        Title = "Bracken Vale";
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) SystemBackdrop = new MicaBackdrop();
        var volume = int.TryParse(_store.GetSetting("volume"), out var savedVolume) ? Math.Clamp(savedVolume, 0, 100) : 75;
        VolumeSlider.Value = volume; _playback.Volume = volume;
        ApplyStoredEqualizer();
        TrackList.ItemsSource = _tracks;
        PlaylistTrackList.ItemsSource = _tracks;
        PlaylistList.ItemsSource = _playlists;
        ApplyStoredNavigation();
        NavView.SelectedItem = NavView.MenuItems.FirstOrDefault();
        _playback.TrackEnded += Playback_TrackEnded;
        _playback.CrossfadeCompleted += Playback_CrossfadeCompleted;
        _playback.CrossfadeFailed += Playback_CrossfadeFailed;
        _playback.PlaybackFailed += Playback_Failed;
        InitializeSystemMediaControls();
        ApplyTraySetting();
        _clock.Tick += Clock_Tick;
        _clock.Start();
        Closed += MainWindow_Closed;
        ApplyStoredAppearance();
        RestoreSession();
        RefreshLibrary();
        RefreshPlaylists();
        StartStartupScan();
        _ = CheckForUpdatesAsync(false);
    }

    private void StartStartupScan()
    {
        var roots = ReadJsonSetting("library-roots", Array.Empty<string>()).ToList();
        if (roots.Count == 0)
        {
            roots = LibraryScanner.DefaultRoots().ToList();
            _store.SetSetting("library-roots", JsonSerializer.Serialize(roots));
        }
        if (roots.Count > 0) StartScan(roots);
    }

    private async void StartScan(IEnumerable<string> roots)
    {
        if (_scanControl is not null) return;
        var scanRoots = roots.Select(Path.GetFullPath).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
        if (scanRoots.Length == 0) return;
        var control = new ScanControl();
        _scanControl = control;
        AddFolderButton.IsEnabled = ScanLibraryButton.IsEnabled = false;
        ScanStatus.Visibility = Visibility.Visible; ScanPauseButton.Visibility = Visibility.Visible; ScanPauseButton.Content = "Pause scan";
        ScanProgress.IsIndeterminate = true; ScanStatusText.Text = "Preparing library scan…";
        try
        {
            var ignored = ReadJsonSetting("ignored-directories", Array.Empty<string>());
            var indexer = new LibraryIndexer(_store, Path.Combine(_appData, "Artwork"));
            var nextLibraryRefresh = 256;
            var progress = new Progress<ScanProgress>(value =>
            {
                ScanStatusText.Text = $"{value.FilesFound:N0} tracks · {value.DirectoriesVisited:N0} folders";
                if (value.FilesFound >= nextLibraryRefresh) { RefreshLibrary(); nextLibraryRefresh = value.FilesFound + 256; }
            });
            var result = await Task.Run(() => indexer.ScanAsync(scanRoots, ignored, control, progress));
            ScanStatusText.Text = $"Indexed {result.Indexed:N0} · removed {result.Removed:N0} · skipped {result.Skipped:N0}";
        }
        catch (OperationCanceledException) { LocalAppLog.Shared.Info("scanner", "Library scan cancelled; completed tracks were retained."); ScanStatusText.Text = "Scan cancelled; completed tracks are saved."; }
        catch (Exception ex) { LocalAppLog.Shared.Error("scanner", "Library scan failed.", ex); ScanStatusText.Text = $"Scan stopped: {ex.Message}"; }
        finally
        {
            control.Dispose(); _scanControl = null; AddFolderButton.IsEnabled = ScanLibraryButton.IsEnabled = true; ScanPauseButton.Visibility = Visibility.Collapsed;
            ConfigureGroupView();
            _ = Task.Delay(4500).ContinueWith(_ => DispatcherQueue.TryEnqueue(() => ScanStatus.Visibility = Visibility.Collapsed));
            RefreshLibrary();
        }
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        var roots = ReadJsonSetting("library-roots", Array.Empty<string>()).ToList();
        if (!roots.Contains(folder.Path, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)) roots.Add(folder.Path);
        _store.SetSetting("library-roots", JsonSerializer.Serialize(roots));
        StartScan([folder.Path]);
    }

    private void Scan_Click(object sender, RoutedEventArgs e)
    {
        var roots = ReadJsonSetting("library-roots", Array.Empty<string>());
        if (roots.Length == 0) roots = LibraryScanner.DefaultRoots().ToArray();
        StartScan(roots);
    }

    private void ScanPause_Click(object sender, RoutedEventArgs e)
    {
        if (_scanControl is null) return;
        if (ScanPauseButton.Content?.ToString() == "Pause scan")
        { _scanControl.Pause(); ScanPauseButton.Content = "Resume scan"; ScanStatusText.Text = "Scan paused"; }
        else { _scanControl.Resume(); ScanPauseButton.Content = "Pause scan"; }
    }

    private void ScanCancel_Click(object sender, RoutedEventArgs e) => _scanControl?.Cancel();

    private void RefreshLibrary()
    {
        var search = SearchBox?.Text;
        IReadOnlyList<Track> source;
        if (_selectedPlaylist is not null)
        {
            source = _selectedPlaylist.Paths.Select(path => _store.GetTrack(path)).Where(track => track is not null).Cast<Track>().ToArray();
            if (!string.IsNullOrWhiteSpace(search)) source = source.Where(track => Contains(track.Title, search) || Contains(track.Artist, search) || Contains(track.Album, search) || Contains(track.AlbumArtist, search) || Contains(track.Genre, search) || Contains(track.Path, search)).ToArray();
        }
        else
        {
            var filter = _view switch { "Favorites" => "favorites", "Most Played" => "most-played", "Recently Played" => "recent", _ => null };
            source = _store.GetTracks(search, _sort, _descending, filter, GroupColumn(), GroupList.SelectedItem?.ToString());
        }
        _tracks.Clear(); foreach (var track in source) _tracks.Add(track);
        TrackCountText.Text = $"{_tracks.Count:N0} {(_tracks.Count == 1 ? "track" : "tracks")}";
        EmptyState.Visibility = _tracks.Count == 0 && _selectedPlaylist is null ? Visibility.Visible : Visibility.Collapsed;
        PlaylistView.Visibility = _view == "Playlists" && _selectedPlaylist is null ? Visibility.Visible : Visibility.Collapsed;
        LibraryView.Visibility = _view != "Now Playing" && PlaylistView.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        NowPlayingView.Visibility = _view == "Now Playing" ? Visibility.Visible : Visibility.Collapsed;
        if (_selectedPlaylist is not null) ViewTitle.Text = _selectedPlaylist.Name;
        else ViewTitle.Text = GroupList.SelectedItem is string group ? $"{_view} · {group}" : _view;
    }

    private static bool Contains(string value, string query) => value.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var item = args.SelectedItem as NavigationViewItem;
        if (item?.Tag?.ToString() == "Settings") { _ = ShowSettingsAsync(); return; }
        var next = item?.Tag?.ToString() ?? "Songs";
        if (next == "Playlists") { _selectedPlaylist = null; RefreshPlaylists(); }
        else _selectedPlaylist = null;
        _view = next;
        GroupList.SelectedIndex = -1;
        ConfigureGroupView();
        _sort = next switch { "Albums" => TrackSort.Album, "Artists" => TrackSort.Artist, "Genres" => TrackSort.Genre, "Folders" => TrackSort.Path,
            "Recently Added" => TrackSort.Added, "Most Played" => TrackSort.PlayCount, "Recently Played" => TrackSort.LastPlayed, _ => TrackSort.Title };
        _descending = next is "Recently Added" or "Most Played" or "Recently Played";
        if (SortBox is not null) SortBox.SelectedIndex = _sort switch
        {
            TrackSort.Artist => 1, TrackSort.Album => 2, TrackSort.Genre => 3, TrackSort.Year => 4,
            TrackSort.Added => 5, TrackSort.Duration => 6, TrackSort.PlayCount => 7, TrackSort.LastPlayed => 8,
            TrackSort.Path => 9, TrackSort.Rating => 10, _ => 0
        };
        SortDirectionButton.Content = _descending ? "Descending" : "Ascending";
        RefreshLibrary();
    }

    private string? GroupColumn() => _view switch { "Albums" => "album", "Artists" => "artist", "Genres" => "genre", "Folders" => "folder", _ => null };

    private void ConfigureGroupView()
    {
        var column = GroupColumn();
        var grouped = column is not null;
        GroupList.Visibility = grouped ? Visibility.Visible : Visibility.Collapsed;
        GroupHeading.Visibility = grouped ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(TrackHeader, grouped ? 1 : 0); Grid.SetColumnSpan(TrackHeader, grouped ? 1 : 2);
        Grid.SetColumn(TrackList, grouped ? 1 : 0); Grid.SetColumnSpan(TrackList, grouped ? 1 : 2);
        if (column is null) { GroupList.ItemsSource = null; return; }
        var selected = GroupList.SelectedItem?.ToString();
        var values = column == "folder" ? _store.GetFolders() : _store.GetGroups(column);
        GroupList.ItemsSource = values;
        if (selected is not null && values.Contains(selected, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
            GroupList.SelectedItem = selected;
    }

    private void GroupList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshLibrary();

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshLibrary();

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SortBox.SelectedIndex >= 0 && SortBox.SelectedIndex < 11)
        {
            _sort = SortBox.SelectedIndex switch { 0 => TrackSort.Title, 1 => TrackSort.Artist, 2 => TrackSort.Album, 3 => TrackSort.Genre,
                4 => TrackSort.Year, 5 => TrackSort.Added, 6 => TrackSort.Duration, 7 => TrackSort.PlayCount,
                8 => TrackSort.LastPlayed, 9 => TrackSort.Path, _ => TrackSort.Rating };
            RefreshLibrary();
        }
    }

    private void SortDirection_Click(object sender, RoutedEventArgs e)
    {
        _descending = !_descending; SortDirectionButton.Content = _descending ? "Descending" : "Ascending"; RefreshLibrary();
    }

    private void TrackList_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (TrackList.SelectedItem is Track track) PlayTrack(track, true);
    }

    private void PlaylistTrackList_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (PlaylistTrackList.SelectedItem is Track track) PlayTrack(track, true);
    }

    private void TrackList_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void PlayTrack(Track track, bool resetQueue, int? queueIndex = null)
    {
        if (resetQueue)
        {
            _queue.Clear(); _queue.AddRange(_tracks);
            _queueIndex = _queue.FindIndex(item => item.Path.Equals(track.Path, StringComparison.OrdinalIgnoreCase));
        }
        else if (queueIndex is { } requestedIndex && requestedIndex >= 0 && requestedIndex < _queue.Count) _queueIndex = requestedIndex;
        else
        {
            var matchingIndex = _queue.FindIndex(item => item.Path.Equals(track.Path, StringComparison.OrdinalIgnoreCase));
            if (matchingIndex >= 0) _queueIndex = matchingIndex;
        }
        _playback.Play(track); _countedCurrentPlay = false; _heardMilliseconds = 0; _lastPlayCountPosition = 0; _crossfadeInProgress = false; _crossfadeFailureSource = null; _repeatA = _repeatB = null;
        UpdateCurrentTrack(track); UpdateSystemMediaControls(track, true); PlayPauseButton.Content = "Pause"; SeekSlider.IsEnabled = true; SaveSession();
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_playback.CurrentTrack is null)
        {
            if (_tracks.Count > 0) PlayTrack(_tracks[0], true);
            return;
        }
        if (_playback.IsPlaying) { _playback.Pause(); _crossfadeInProgress = false; if (_systemControls is not null) _systemControls.PlaybackStatus = MediaPlaybackStatus.Paused; PlayPauseButton.Content = "Play"; }
        else { _lastPlayCountPosition = _playback.Position; _playback.PlayLoaded(); UpdateSystemMediaControls(_playback.CurrentTrack, true); PlayPauseButton.Content = "Pause"; }
    }

    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (_playback.Position > 3000) { _playback.Seek(0); return; }
        if (_queueIndex > 0) { _queueIndex--; PlayTrack(_queue[_queueIndex], false, _queueIndex); }
    }

    private void Next_Click(object sender, RoutedEventArgs e) => AdvanceQueue(false);

    private void AdvanceQueue(bool automatic)
    {
        if (_queue.Count == 0) { if (_tracks.Count > 0) { _queue.AddRange(_tracks); _queueIndex = -1; } }
        if (_queue.Count == 0) return;
        if (automatic && _repeatMode == "Track" && _queueIndex >= 0) { PlayTrack(_queue[_queueIndex], false, _queueIndex); return; }
        int next;
        if (_shuffle && _queue.Count > 1)
        {
            do next = Random.Shared.Next(_queue.Count); while (next == _queueIndex);
        }
        else next = _queueIndex + 1;
        if (next >= _queue.Count)
        {
            if (_repeatMode != "Queue") { _playback.Stop(); if (_systemControls is not null) _systemControls.PlaybackStatus = MediaPlaybackStatus.Stopped; PlayPauseButton.Content = "Play"; return; }
            next = 0;
        }
        var track = _queue[next];
        if (_playback.IsPlaying && !SameTrack(_crossfadeFailureSource, _playback.CurrentTrack?.Path) && int.TryParse(_store.GetSetting("crossfade-seconds"), out var seconds) && seconds > 0)
        {
            _queueIndex = next; _crossfadeInProgress = true;
            _ = _playback.CrossfadeToAsync(track, seconds * 1000);
        }
        else PlayTrack(track, false, next);
    }

    private void Shuffle_Click(object sender, RoutedEventArgs e)
    {
        _shuffle = !_shuffle; if (_queue.Count == 0) _queue.AddRange(_tracks);
        SaveSession(); _ = ShowNoticeAsync(_shuffle ? "Shuffle is on." : "Shuffle is off.");
    }

    private void Repeat_Click(object sender, RoutedEventArgs e)
    {
        _repeatMode = _repeatMode switch { "Off" => "Queue", "Queue" => "Track", _ => "Off" };
        RepeatButton.Content = $"Repeat: {_repeatMode}"; SaveSession();
    }

    private void AbRepeat_Click(object sender, RoutedEventArgs e)
    {
        var position = TimeSpan.FromMilliseconds(Math.Max(0, _playback.Position));
        if (_playback.CurrentTrack is null) return;
        if (_repeatA is null) { _repeatA = position; _repeatB = null; _ = ShowNoticeAsync("A–B repeat: mark B at the end of the passage."); }
        else if (_repeatB is null && position > _repeatA) { _repeatB = position; _ = ShowNoticeAsync("A–B repeat is set. Press again to clear."); }
        else { _repeatA = _repeatB = null; _ = ShowNoticeAsync("A–B repeat cleared."); }
    }

    private async void PlayNext_Click(object sender, RoutedEventArgs e)
    {
        if (TrackFromSender(sender) is not { } track) return;
        if (_queue.Count == 0) _queue.AddRange(_tracks);
        var insertAt = Math.Clamp(_queueIndex + 1, 0, _queue.Count);
        _queue.Insert(insertAt, track); if (_queueIndex >= 0 && insertAt <= _queueIndex) _queueIndex++;
        await ShowNoticeAsync($"{track.Title} will play next."); SaveSession();
    }

    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        var path = (sender as FrameworkElement)?.Tag?.ToString(); if (path is null) return;
        var track = _store.GetTrack(path); if (track is null) return;
        _store.SetFavorite(path, !track.Favorite); RefreshLibrary();
    }

    private async void Rating_Click(object sender, RoutedEventArgs e)
    {
        var path = (sender as FrameworkElement)?.Tag?.ToString(); if (path is null || _store.GetTrack(path) is not { } track) return;
        var box = new ComboBox { Header = "Rating", SelectedIndex = track.Rating, MinWidth = 240 };
        foreach (var label in new[] { "Unrated", "★", "★★", "★★★", "★★★★", "★★★★★" }) box.Items.Add(label);
        var dialog = new ContentDialog { Title = $"Rate {track.Title}", Content = box, PrimaryButtonText = "Save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = ShellRoot.XamlRoot };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) { _store.SetRating(path, box.SelectedIndex); RefreshLibrary(); }
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        _playback.Volume = (int)e.NewValue;
        _store.SetSetting("volume", ((int)e.NewValue).ToString());
    }

    private void SeekSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_updatingPosition && SeekSlider.IsEnabled) _playback.Seek((long)e.NewValue);
    }

    private void Clock_Tick(object? sender, object e)
    {
        var position = Math.Max(0, _playback.Position); var duration = Math.Max(0, _playback.Duration);
        _updatingPosition = true; SeekSlider.Maximum = Math.Max(1, duration); SeekSlider.Value = Math.Min(position, SeekSlider.Maximum); _updatingPosition = false;
        ElapsedText.Text = FormatTime(position); DurationText.Text = FormatTime(duration);
        if (_repeatA is not null && _repeatB is not null && position >= _repeatB.Value.TotalMilliseconds) _playback.Seek((long)_repeatA.Value.TotalMilliseconds);
        if (_systemControls is not null && duration > 0)
        {
            var timeline = new SystemMediaTransportControlsTimelineProperties { StartTime = TimeSpan.Zero, EndTime = TimeSpan.FromMilliseconds(duration), Position = TimeSpan.FromMilliseconds(position) };
            _systemControls.UpdateTimelineProperties(timeline);
        }
        if (_playback.CurrentTrack is not null)
        {
            if (_playback.IsPlaying)
            {
                var heardNow = position - _lastPlayCountPosition;
                if (heardNow is > 0 and <= 3000) _heardMilliseconds += heardNow;
            }
            _lastPlayCountPosition = position;
        }
        if (!_countedCurrentPlay && _playback.CurrentTrack is { } track && PlayCompletion.HasReachedHalf(TimeSpan.FromMilliseconds(duration), TimeSpan.FromMilliseconds(_heardMilliseconds)))
        {
            _store.RecordPlayed(track.Path, DateTime.UtcNow); _countedCurrentPlay = true;
        }
        if (_playback.IsPlaying && !_crossfadeInProgress && !SameTrack(_crossfadeFailureSource, _playback.CurrentTrack?.Path) && _queueIndex + 1 < _queue.Count &&
            int.TryParse(_store.GetSetting("crossfade-seconds"), out var crossfade) && crossfade > 0 && duration > 0 && duration - position <= crossfade * 1000)
        {
            _queueIndex++; _crossfadeInProgress = true; _ = _playback.CrossfadeToAsync(_queue[_queueIndex], crossfade * 1000);
        }
        if (DateTime.UtcNow - _lastSessionSave > TimeSpan.FromSeconds(5)) SaveSession();
        if (_playback.CurrentTrack is not null && _currentLyrics.Lines.Count > 0)
            NowPlayingLyrics.Text = _currentLyrics.At(TimeSpan.FromMilliseconds(position));
    }

    private static string FormatTime(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(milliseconds);
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    private void Playback_TrackEnded(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() => AdvanceQueue(true));
    private void Playback_CrossfadeCompleted(Track track) => DispatcherQueue.TryEnqueue(() =>
    {
        _crossfadeInProgress = false; _crossfadeFailureSource = null; _countedCurrentPlay = false; _heardMilliseconds = 0; _lastPlayCountPosition = 0;
        UpdateCurrentTrack(track); UpdateSystemMediaControls(track, true); PlayPauseButton.Content = "Pause";
    });

    private static bool SameTrack(string? left, string? right) => left is not null && right is not null && left.Equals(right, StringComparison.OrdinalIgnoreCase);

    private void Playback_CrossfadeFailed(Track track) => DispatcherQueue.TryEnqueue(() =>
    {
        _crossfadeInProgress = false;
        _crossfadeFailureSource = _playback.CurrentTrack?.Path;
        _queueIndex = _queue.FindIndex(item => SameTrack(item.Path, _playback.CurrentTrack?.Path));
        _ = ShowNoticeAsync($"Could not start {track.Title} during crossfade. Playback will continue; see the local log for details.", InfoBarSeverity.Error);
    });

    private void Playback_Failed(Track track) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!SameTrack(_playback.CurrentTrack?.Path, track.Path)) return;
        _crossfadeInProgress = false;
        PlayPauseButton.Content = "Play";
        if (_systemControls is not null) _systemControls.PlaybackStatus = MediaPlaybackStatus.Stopped;
        _ = ShowNoticeAsync($"Could not play {track.Title}. See Settings → Open log folder for details.", InfoBarSeverity.Error);
    });

    private void UpdateCurrentTrack(Track track)
    {
        PlayerTitle.Text = track.Title; PlayerArtist.Text = track.Artist;
        NowPlayingTitle.Text = track.Title; NowPlayingArtist.Text = track.Artist;
        var rawLyrics = LyricsFiles.ReadRaw(track.Path);
        _currentLyrics = Lyrics.Parse(rawLyrics);
        var currentLyrics = Lyrics.DisplayAt(_currentLyrics, rawLyrics, TimeSpan.FromMilliseconds(_playback.Position));
        NowPlayingLyrics.Text = currentLyrics.Length == 0 && _currentLyrics.Lines.Count == 0 ? "No lyrics. Open lyrics to add or search." : currentLyrics;
        SetArtwork(NowPlayingArtwork, track.ArtworkPath);
        if (_store.GetSetting("accent-mode") == "Artwork" && _store.GetSetting("accent-manual") != "true") _ = ApplyArtworkAccentAsync(track.ArtworkPath);
    }

    private void Artwork_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Image image) SetArtwork(image, image.Tag?.ToString());
    }

    private static void SetArtwork(Image image, string? path)
    {
        image.Source = !string.IsNullOrWhiteSpace(path) && File.Exists(path)
            ? new BitmapImage(new Uri(path))
            : null;
    }

    private void ApplyStoredEqualizer()
    {
        var selected = _store.GetSetting("eq-current");
        if (string.IsNullOrWhiteSpace(selected) || selected == "Off") return;
        var saved = _store.GetSetting(selected == "Custom" ? "eq-bands" : "eq-preset:" + selected);
        if (saved is not null)
        {
            _playback.ApplyEqualizer(null, ReadEqualizerBands(saved, PlaybackService.EqualizerBands().Count));
            return;
        }
        if (PlaybackService.EqualizerPresets().Contains(selected, StringComparer.OrdinalIgnoreCase)) _playback.ApplyEqualizer(selected);
    }

    private static float[] ReadEqualizerBands(string? json, int count)
    {
        if (json is null) return new float[count];
        try
        {
            var saved = JsonSerializer.Deserialize<float[]>(json) ?? [];
            return Enumerable.Range(0, count).Select(index => index < saved.Length && float.IsFinite(saved[index])
                ? Math.Clamp(saved[index], -20, 20) : 0).ToArray();
        }
        catch (JsonException ex)
        {
            LocalAppLog.Shared.Warning("equalizer", "Saved equalizer settings were invalid.", ex);
            return new float[count];
        }
    }

    private void RestoreSession()
    {
        var session = _store.LoadSession(); if (session is null) return;
        _shuffle = session.Shuffle; _repeatMode = session.RepeatMode;
        RepeatButton.Content = $"Repeat: {_repeatMode}";
        _queue.AddRange(session.Queue.Select(path => _store.GetTrack(path)).Where(track => track is not null).Cast<Track>());
        if (session.TrackPath is { } path && _store.GetTrack(path) is { } current)
        {
            if (_queue.Count == 0) _queue.Add(current);
            _queueIndex = _queue.FindIndex(track => track.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            _playback.LoadPaused(current, session.PositionMilliseconds); _countedCurrentPlay = false; _heardMilliseconds = 0; _lastPlayCountPosition = session.PositionMilliseconds;
            UpdateCurrentTrack(current); UpdateSystemMediaControls(current, false); SeekSlider.IsEnabled = true; PlayPauseButton.Content = "Play";
        }
    }

    private void SaveSession()
    {
        try
        {
            _store.SaveSession(new(_playback.CurrentTrack?.Path, Math.Max(0, _playback.Position), _queue.Select(track => track.Path).ToArray(), _shuffle, _repeatMode));
            _lastSessionSave = DateTime.UtcNow;
        }
        catch (Exception ex) { LocalAppLog.Shared.Error("session", "Could not save playback state.", ex); }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        LocalAppLog.Shared.Info("app", "Window closed.");
        SaveSession(); _clock.Stop(); _playback.Dispose(); _scanControl?.Cancel();
        _tray?.Dispose();
    }

    private void ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = (sender as FrameworkElement)?.Tag?.ToString(); if (path is null) return;
        var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = true }; info.ArgumentList.Add("/select," + path); Process.Start(info);
    }

    private void OpenLogsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            info.ArgumentList.Add(LocalAppLog.Shared.FolderPath);
            Process.Start(info);
        }
        catch (Exception ex)
        {
            LocalAppLog.Shared.Error("logs", "Could not open the log folder.", ex);
            _ = ShowNoticeAsync($"Logs are stored at {LocalAppLog.Shared.FolderPath}");
        }
    }

    private Track? TrackFromSender(object sender)
    {
        var path = (sender as FrameworkElement)?.Tag?.ToString(); return path is null ? null : _store.GetTrack(path);
    }

    private T[] ReadJsonSetting<T>(string key, T[] fallback)
    {
        try { return _store.GetSetting(key) is { } json ? JsonSerializer.Deserialize<T[]>(json) ?? fallback : fallback; }
        catch (JsonException ex) { LocalAppLog.Shared.Warning("settings", $"Saved setting '{key}' was invalid JSON.", ex); return fallback; }
    }

    private void ApplyStoredNavigation()
    {
        var items = NavView.MenuItems.OfType<NavigationViewItem>().ToList();
        var byTag = items.Where(item => item.Tag is not null).ToDictionary(item => item.Tag!.ToString()!, StringComparer.Ordinal);
        var order = ReadJsonSetting("navigation-order", Array.Empty<string>());
        var arranged = order.Where(byTag.ContainsKey).Select(key => byTag[key]).Concat(items.Where(item => !order.Contains(item.Tag?.ToString() ?? "", StringComparer.Ordinal))).ToArray();
        NavView.MenuItems.Clear();
        foreach (var item in arranged) NavView.MenuItems.Add(item);
        var hidden = ReadJsonSetting("hidden-panels", Array.Empty<string>()).ToHashSet(StringComparer.Ordinal);
        foreach (var item in arranged) item.Visibility = hidden.Contains(item.Tag?.ToString() ?? "") ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyStoredAppearance()
    {
        ShellRoot.RequestedTheme = _store.GetSetting("theme") switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        if (_store.GetSetting("accent-manual") == "true" && TryParseColor(_store.GetSetting("accent-color"), out var color)) ApplyAccent(color);
    }

    private async Task ShowSettingsAsync()
    {
        var navigationSnapshot = NavView.MenuItems.OfType<NavigationViewItem>().Select(item => (Item: item, item.Visibility)).ToArray();
        void RestoreNavigation()
        {
            NavView.MenuItems.Clear();
            foreach (var (item, visibility) in navigationSnapshot) { item.Visibility = visibility; NavView.MenuItems.Add(item); }
            SelectVisibleLibraryNavigation();
        }
        var content = new StackPanel { Spacing = 12, Margin = new Thickness(4) };
        var scroll = new ScrollViewer { Content = content, MaxHeight = 640, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var theme = new ComboBox { Header = "Color theme", MinWidth = 260 };
        theme.Items.Add("System"); theme.Items.Add("Light"); theme.Items.Add("Dark");
        theme.SelectedItem = _store.GetSetting("theme") ?? "System";
        var accentMode = new ComboBox { Header = "Accent style", MinWidth = 260 };
        accentMode.Items.Add("Windows Fluent"); accentMode.Items.Add("Album-art emphasis");
        accentMode.SelectedIndex = _store.GetSetting("accent-mode") == "Artwork" ? 1 : 0;
        var updateCheck = new ToggleSwitch { Header = "Check GitHub Releases weekly", IsOn = _store.GetSetting("check-updates") != "false" };
        var minimizeToTray = new ToggleSwitch { Header = "Minimize to the notification area", IsOn = _store.GetSetting("minimize-to-tray") == "true" };
        var manualAccent = new ToggleSwitch { Header = "Use a custom accent color", IsOn = _store.GetSetting("accent-manual") == "true" };
        var color = TryParseColor(_store.GetSetting("accent-color"), out var savedColor) ? savedColor : Color.FromArgb(255, 62, 125, 96);
        var colorPicker = new ColorPicker { Color = color, IsColorPreviewVisible = true, IsColorSliderVisible = true, IsColorChannelTextInputVisible = true, IsHexInputVisible = true };
        var crossfade = new ComboBox { Header = "Crossfade", MinWidth = 260 };
        foreach (var value in new[] { "Off", "2 seconds", "3 seconds", "5 seconds", "8 seconds", "10 seconds" }) crossfade.Items.Add(value);
        var oldCrossfade = int.TryParse(_store.GetSetting("crossfade-seconds"), out var seconds) ? seconds : 0;
        crossfade.SelectedIndex = oldCrossfade switch { 2 => 1, 3 => 2, 5 => 3, 8 => 4, 10 => 5, _ => 0 };
        var ignored = new TextBox { Header = "Ignored folders (one full path per line)", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 84, Text = string.Join(Environment.NewLine, ReadJsonSetting("ignored-directories", Array.Empty<string>())) };
        content.Children.Add(theme); content.Children.Add(accentMode); content.Children.Add(updateCheck); content.Children.Add(minimizeToTray);
        var updateNow = new Button { Content = "Check for updates now" };
        var updateStatus = new TextBlock { Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], TextWrapping = TextWrapping.Wrap };
        updateNow.Click += async (_, _) => await CheckForUpdatesAsync(true, updateStatus);
        content.Children.Add(updateNow); content.Children.Add(updateStatus);
        content.Children.Add(manualAccent);
        content.Children.Add(new TextBlock { Text = "Custom accent", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
        content.Children.Add(colorPicker); content.Children.Add(crossfade); content.Children.Add(ignored);
        content.Children.Add(new TextBlock { Text = $"Crash and error logs stay on this PC:\n{LocalAppLog.Shared.FolderPath}", TextWrapping = TextWrapping.Wrap });
        var openLogs = new Button { Content = "Open log folder", HorizontalAlignment = HorizontalAlignment.Left };
        openLogs.Click += OpenLogsFolder_Click; content.Children.Add(openLogs);
        content.Children.Add(new TextBlock { Text = "Library navigation · reorder with arrows, hide optional panels", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], Margin = new Thickness(0, 12, 0, 0) });
        var navigationList = new StackPanel { Spacing = 4 };
        foreach (var item in NavView.MenuItems.OfType<NavigationViewItem>().ToArray())
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var visible = new CheckBox { Content = item.Content, IsChecked = item.Visibility == Visibility.Visible, VerticalAlignment = VerticalAlignment.Center };
            visible.Checked += (_, _) => item.Visibility = Visibility.Visible; visible.Unchecked += (_, _) => item.Visibility = Visibility.Collapsed;
            var up = new Button { Content = "↑", Tag = item, Margin = new Thickness(4, 0, 0, 0) };
            var down = new Button { Content = "↓", Tag = item, Margin = new Thickness(4, 0, 0, 0) };
            AutomationProperties.SetName(up, "Move navigation item up"); AutomationProperties.SetName(down, "Move navigation item down");
            up.Click += (_, _) => MoveNavigationItem(item, -1); down.Click += (_, _) => MoveNavigationItem(item, 1);
            Grid.SetColumn(up, 1); Grid.SetColumn(down, 2); row.Children.Add(visible); row.Children.Add(up); row.Children.Add(down); navigationList.Children.Add(row);
        }
        content.Children.Add(navigationList);
        var dialog = new ContentDialog { Title = "Settings", Content = scroll, PrimaryButtonText = "Save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = ShellRoot.XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            RestoreNavigation();
            return;
        }
        var chosenTheme = theme.SelectedItem?.ToString() ?? "System";
        var chosenAccent = accentMode.SelectedIndex == 1 ? "Artwork" : "Native";
        var crossfadeSeconds = crossfade.SelectedIndex switch { 1 => 2, 2 => 3, 3 => 5, 4 => 8, 5 => 10, _ => 0 };
        string[] ignoredPaths;
        try
        {
            ignoredPaths = ignored.Text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(path => Path.GetFullPath(path)).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { RestoreNavigation(); await ShowNoticeAsync($"One ignored folder path is invalid: {ex.Message}", InfoBarSeverity.Warning); return; }
        _store.SetSetting("theme", chosenTheme); _store.SetSetting("accent-mode", chosenAccent);
        _store.SetSetting("accent-manual", manualAccent.IsOn ? "true" : "false"); _store.SetSetting("accent-color", $"#{colorPicker.Color.R:X2}{colorPicker.Color.G:X2}{colorPicker.Color.B:X2}");
        _store.SetSetting("check-updates", updateCheck.IsOn ? "true" : "false"); _store.SetSetting("crossfade-seconds", crossfadeSeconds.ToString());
        _store.SetSetting("minimize-to-tray", minimizeToTray.IsOn ? "true" : "false"); ApplyTraySetting();
        _store.SetSetting("ignored-directories", JsonSerializer.Serialize(ignoredPaths));
        _store.SetSetting("navigation-order", JsonSerializer.Serialize(NavView.MenuItems.OfType<NavigationViewItem>().Select(item => item.Tag?.ToString())));
        _store.SetSetting("hidden-panels", JsonSerializer.Serialize(NavView.MenuItems.OfType<NavigationViewItem>().Where(item => item.Visibility != Visibility.Visible).Select(item => item.Tag?.ToString())));
        ApplyStoredAppearance();
        if (manualAccent.IsOn) ApplyAccent(colorPicker.Color);
        else if (chosenAccent == "Artwork" && _playback.CurrentTrack is { } current) await ApplyArtworkAccentAsync(current.ArtworkPath);
        else ResetAccent();
        RefreshLibrary();
        SelectVisibleLibraryNavigation();
        if (_store.GetSetting("check-updates") == "true") _ = CheckForUpdatesAsync(false);
    }

    private void SelectVisibleLibraryNavigation()
    {
        var visible = NavView.MenuItems.OfType<NavigationViewItem>().Where(item => item.Visibility == Visibility.Visible).ToArray();
        var selected = visible.FirstOrDefault(item => item.Tag?.ToString() == _view) ?? visible.FirstOrDefault();
        if (selected is not null) NavView.SelectedItem = selected;
    }

    private void MoveNavigationItem(NavigationViewItem item, int direction)
    {
        var index = NavView.MenuItems.IndexOf(item); var destination = index + direction;
        if (index < 0 || destination < 0 || destination >= NavView.MenuItems.Count) return;
        NavView.MenuItems.RemoveAt(index); NavView.MenuItems.Insert(destination, item);
    }

    private async Task<bool> ApplyArtworkAccentAsync(string? artworkPath)
    {
        if (string.IsNullOrWhiteSpace(artworkPath) || !System.IO.File.Exists(artworkPath)) { ResetAccent(); return false; }
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(artworkPath);
            using var stream = await file.OpenAsync(FileAccessMode.Read);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Rgba8, BitmapAlphaMode.Ignore,
                new BitmapTransform { ScaledWidth = 1, ScaledHeight = 1 }, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            var pixels = data.DetachPixelData();
            if (pixels.Length < 3) { ResetAccent(); return false; }
            ApplyAccent(Color.FromArgb(255, pixels[0], pixels[1], pixels[2]));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        { LocalAppLog.Shared.Warning("artwork-accent", $"Could not read artwork '{artworkPath}'.", ex); ResetAccent(); return false; }
    }

    private static void ApplyAccent(Color color)
    {
        Application.Current.Resources["SystemAccentColor"] = color;
        Application.Current.Resources["SystemAccentColorLight1"] = Blend(color, Colors.White, .3);
        Application.Current.Resources["SystemAccentColorLight2"] = Blend(color, Colors.White, .55);
        Application.Current.Resources["SystemAccentColorDark1"] = Blend(color, Colors.Black, .24);
        Application.Current.Resources["SystemAccentColorDark2"] = Blend(color, Colors.Black, .46);
    }

    private static Color Blend(Color color, Color background, double amount) => Color.FromArgb(255,
        (byte)(color.R * (1 - amount) + background.R * amount), (byte)(color.G * (1 - amount) + background.G * amount), (byte)(color.B * (1 - amount) + background.B * amount));

    private static void ResetAccent()
    {
        foreach (var key in new[] { "SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorDark1", "SystemAccentColorDark2" }) Application.Current.Resources.Remove(key);
    }

    private static bool TryParseColor(string? value, out Color color)
    {
        color = Colors.ForestGreen;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var hex = value.Trim().TrimStart('#');
        if (hex.Length != 6 || !byte.TryParse(hex[..2], System.Globalization.NumberStyles.HexNumber, null, out var r) ||
            !byte.TryParse(hex[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g) ||
            !byte.TryParse(hex[4..], System.Globalization.NumberStyles.HexNumber, null, out var b)) return false;
        color = Color.FromArgb(255, r, g, b); return true;
    }

    private async Task CheckForUpdatesAsync(bool force, TextBlock? statusTarget = null)
    {
        if (!force && _store.GetSetting("check-updates") == "false") return;
        if (!force && DateTime.TryParse(_store.GetSetting("last-update-check"), out var last) && DateTime.UtcNow - last.ToUniversalTime() < TimeSpan.FromDays(7)) return;
        try
        {
            _store.SetSetting("last-update-check", DateTime.UtcNow.ToString("O"));
            var release = await GitHubUpdates.GetLatestAsync();
            var current = typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 1, 0);
            if (release is null || !Version.TryParse(release.Tag.TrimStart('v'), out var latest) || latest <= current)
            {
                if (force)
                {
                    const string message = "You are using the latest available release.";
                    if (statusTarget is null) await ShowNoticeAsync(message); else statusTarget.Text = message;
                }
                return;
            }
            ReleaseNotice.Title = $"Bracken Vale {release.Tag} is available";
            ReleaseNotice.Message = "A new release is ready to view. Updates are never downloaded automatically.";
            var open = new Button { Content = "View release" }; open.Click += (_, _) => Process.Start(new ProcessStartInfo(release.Url) { UseShellExecute = true });
            ReleaseNotice.ActionButton = open; ReleaseNotice.IsOpen = true;
            if (statusTarget is not null) statusTarget.Text = $"Bracken Vale {release.Tag} is available.";
        }
        catch (HttpRequestException ex) { LocalAppLog.Shared.Warning("updates", "GitHub release check failed.", ex); if (force) await ReportUpdateCheckStatusAsync(statusTarget, "Could not reach GitHub. Check your connection and try again."); }
        catch (TaskCanceledException ex) { LocalAppLog.Shared.Warning("updates", "GitHub release check timed out.", ex); if (force) await ReportUpdateCheckStatusAsync(statusTarget, "The GitHub update check timed out."); }
        catch (System.Text.Json.JsonException ex) { LocalAppLog.Shared.Warning("updates", "GitHub returned invalid release data.", ex); if (force) await ReportUpdateCheckStatusAsync(statusTarget, "GitHub returned an update response Bracken Vale could not read."); }
        catch (Exception ex) { LocalAppLog.Shared.Error("updates", "Could not complete the GitHub release check.", ex); if (force) await ReportUpdateCheckStatusAsync(statusTarget, "The update check failed. See the local log for details."); }
    }

    private async Task ReportUpdateCheckStatusAsync(TextBlock? statusTarget, string message)
    {
        if (statusTarget is null) await ShowNoticeAsync(message, InfoBarSeverity.Warning); else statusTarget.Text = message;
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(true);

    private void RefreshPlaylists()
    {
        var selectedId = _selectedPlaylist?.Id;
        _playlists.Clear(); foreach (var playlist in _store.GetPlaylists()) _playlists.Add(playlist);
        if (selectedId is not null) PlaylistList.SelectedItem = _playlists.FirstOrDefault(item => item.Id == selectedId);
    }

    private void PlaylistList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedPlaylist = PlaylistList.SelectedItem as Playlist;
        RefreshLibrary();
    }

    private async void NewPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var name = new TextBox { PlaceholderText = "Playlist name", MinWidth = 280 };
        var dialog = new ContentDialog { Title = "Create playlist", Content = name, PrimaryButtonText = "Create", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = ShellRoot.XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(name.Text)) return;
        _store.CreatePlaylist(name.Text); RefreshPlaylists();
    }

    private async void ImportPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".m3u"); picker.FileTypeFilter.Add(".m3u8");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync(); if (file is null) return;
        try { _store.ImportM3u8(file.Path); RefreshPlaylists(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { LocalAppLog.Shared.Error("playlist-import", "Could not import a playlist.", ex); await ShowNoticeAsync($"Could not import that playlist: {ex.Message}", InfoBarSeverity.Error); }
    }

    private async void ExportPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPlaylist is null) { await ShowNoticeAsync("Select a playlist first."); return; }
        var picker = new FileSavePicker(); picker.FileTypeChoices.Add("M3U8 playlist", [".m3u8"]); picker.SuggestedFileName = _selectedPlaylist.Name;
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync(); if (file is null) return;
        try { _store.ExportM3u8(_selectedPlaylist.Id, file.Path); await ShowNoticeAsync("Playlist exported as UTF-8 M3U8."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { LocalAppLog.Shared.Error("playlist-export", "Could not export a playlist.", ex); await ShowNoticeAsync($"Could not export that playlist: {ex.Message}", InfoBarSeverity.Error); }
    }

    private async void DeletePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPlaylist is null) { await ShowNoticeAsync("Select a playlist first."); return; }
        var confirm = new ContentDialog
        {
            Title = $"Delete {_selectedPlaylist.Name}?",
            Content = "This removes the local playlist and its entries. Music files are not changed.",
            PrimaryButtonText = "Delete", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close, XamlRoot = ShellRoot.XamlRoot
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        _store.DeletePlaylist(_selectedPlaylist.Id); _selectedPlaylist = null; RefreshPlaylists(); RefreshLibrary();
    }

    private async void AddToPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (TrackFromSender(sender) is not { } track) return;
        RefreshPlaylists();
        if (_playlists.Count == 0) { await ShowNoticeAsync("Create a playlist first."); return; }
        var chooser = new ComboBox { ItemsSource = _playlists, DisplayMemberPath = "Name", SelectedIndex = 0, MinWidth = 280 };
        var dialog = new ContentDialog { Title = $"Add {track.Title} to playlist", Content = chooser, PrimaryButtonText = "Add", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = ShellRoot.XamlRoot };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && chooser.SelectedItem is Playlist playlist)
        {
            _store.AddToPlaylist(playlist.Id, [track.Path]); RefreshPlaylists(); await ShowNoticeAsync($"Added to {playlist.Name}.");
        }
    }

    private void RemoveFromPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPlaylist is null) return;
        DependencyObject? container = sender as DependencyObject;
        while (container is not null && container is not ListViewItem) container = VisualTreeHelper.GetParent(container);
        if (container is not ListViewItem row) return;
        var rowIndex = PlaylistTrackList.IndexFromContainer(row);
        if (rowIndex < 0 || rowIndex >= _tracks.Count) return;
        var path = _tracks[rowIndex].Path;
        var occurrence = _tracks.Take(rowIndex + 1).Count(track => track.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) - 1;
        var position = Playlists.FindPathOccurrence(_selectedPlaylist.Paths, path, occurrence);
        if (position < 0) return;
        _store.RemoveFromPlaylist(_selectedPlaylist.Id, position);
        _selectedPlaylist = _store.GetPlaylists().FirstOrDefault(item => item.Id == _selectedPlaylist.Id);
        RefreshLibrary(); RefreshPlaylists();
    }

    private async void EditTags_Click(object sender, RoutedEventArgs e)
    {
        if (TrackFromSender(sender) is not { } track) return;
        IReadOnlyDictionary<string, string> customValues;
        try { customValues = TagEditor.ReadCustomFields(track.Path); }
        catch (Exception ex)
        {
            LocalAppLog.Shared.Error("tag-editor", $"Could not read custom tags for {track.Path}.", ex);
            await ShowNoticeAsync($"Could not read this file's custom tags: {ex.Message}");
            return;
        }
        var title = AddTextField("Title", track.Title); var artist = AddTextField("Artist", track.Artist);
        var album = AddTextField("Album", track.Album); var albumArtist = AddTextField("Album artist", track.AlbumArtist); var genre = AddTextField("Genre", track.Genre);
        var year = AddTextField("Year", track.Year == 0 ? "" : track.Year.ToString()); var number = AddTextField("Track number", track.TrackNumber == 0 ? "" : track.TrackNumber.ToString());
        var artwork = AddTextField("Artwork file (optional)", "");
        var artworkPreview = new Image { Width = 96, Height = 96, Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill };
        SetArtwork(artworkPreview, track.ArtworkPath);
        var customFormat = TagEditor.CustomFieldFormat(track.Path);
        var custom = new TextBox
        {
            Header = customFormat is null ? "Custom fields are unsupported for this file format" : $"{customFormat} (KEY=value, one per line; blank value removes a field)",
            AcceptsReturn = true, MinHeight = 90, TextWrapping = TextWrapping.Wrap, IsEnabled = customFormat is not null,
            Text = string.Join(Environment.NewLine, customValues.Select(field => $"{field.Key}={field.Value}"))
        };
        var artPicker = new Button { Content = "Choose artwork…", HorizontalAlignment = HorizontalAlignment.Left };
        artPicker.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker(); foreach (var ext in new[] { ".jpg", ".jpeg", ".png", ".webp" }) picker.FileTypeFilter.Add(ext);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this)); var file = await picker.PickSingleFileAsync();
            if (file is not null) { artwork.Text = file.Path; SetArtwork(artworkPreview, file.Path); }
        };
        var fields = new StackPanel { Spacing = 8, Margin = new Thickness(2) };
        fields.Children.Add(new TextBlock { Text = track.Path, TextWrapping = TextWrapping.Wrap, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        foreach (var field in new[] { title, artist, album, albumArtist, genre, year, number, artwork }) fields.Children.Add(field);
        fields.Children.Add(artPicker); fields.Children.Add(artworkPreview); fields.Children.Add(custom);
        var scroll = new ScrollViewer { Content = fields, MaxHeight = 620, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var dialog = new ContentDialog { Title = "Preview and edit tags", Content = scroll, PrimaryButtonText = "Save tags", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = ShellRoot.XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        TagBackup? backup = null;
        var tagsWritten = false;
        try
        {
            var customFields = ParseCustomFields(custom.Text);
            var edit = new TagEdit(title.Text, artist.Text, album.Text, albumArtist.Text, genre.Text,
                uint.TryParse(year.Text, out var parsedYear) ? parsedYear : null,
                uint.TryParse(number.Text, out var parsedNumber) ? parsedNumber : null,
                ArtworkPath: string.IsNullOrWhiteSpace(artwork.Text) ? null : artwork.Text,
                CustomFields: customFormat is null ? null : customFields);
            backup = new TagEditor(Path.Combine(_appData, "TagBackups")).Save(track.Path, edit);
            tagsWritten = true;
            _store.RecordTagBackup(backup);
            RefreshEditedTrack(track.Path);
        }
        catch (Exception ex)
        {
            if (tagsWritten)
            {
                LocalAppLog.Shared.Warning("tag-editor", $"Tags were written but the library refresh failed for {track.Path}. Backup: {backup?.BackupPath}", ex);
                await ShowNoticeAsync($"Tags were written, but the library could not refresh. Backup: {backup?.BackupPath}", InfoBarSeverity.Warning);
            }
            else
            {
                LocalAppLog.Shared.Error("tag-editor", $"Could not save tags for {track.Path}.", ex);
                await ShowNoticeAsync($"Tags were not saved. The original file is intact. {ex.Message}", InfoBarSeverity.Error);
            }
        }
    }

    private void RefreshEditedTrack(string path)
    {
        var track = TrackReader.Read(path, Path.Combine(_appData, "Artwork"));
        _store.UpsertTrack(track);
        if (SameTrack(_playback.CurrentTrack?.Path, track.Path))
        {
            _playback.UpdateTrackMetadata(track);
            UpdateCurrentTrack(track);
            UpdateSystemMediaControls(track, _playback.IsPlaying);
        }
        RefreshLibrary();
    }

    private static TextBox AddTextField(string name, string value) => new() { Header = name, Text = value, MinWidth = 330 };

    private static IReadOnlyDictionary<string, string> ParseCustomFields(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = line.IndexOf('='); if (equals <= 0) throw new ArgumentException("Custom fields must use KEY=value, one per line.");
            fields[line[..equals].Trim()] = line[(equals + 1)..].Trim();
        }
        return fields;
    }

    private async void RestoreTags_Click(object sender, RoutedEventArgs e)
    {
        var track = TrackFromSender(sender); if (track is null) return;
        var backup = _store.GetLatestTagBackup(track.Path);
        if (backup is null) { await ShowNoticeAsync("No tag backup is saved for this track."); return; }
        var confirm = new ContentDialog { Title = "Restore previous tags?", Content = "The file will be replaced with the recoverable copy saved before the last tag edit.", PrimaryButtonText = "Restore", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = ShellRoot.XamlRoot };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        var restored = false;
        try
        {
            new TagEditor(Path.Combine(_appData, "TagBackups")).Restore(backup);
            restored = true;
            RefreshEditedTrack(track.Path);
        }
        catch (Exception ex)
        {
            if (restored)
            {
                LocalAppLog.Shared.Warning("tag-restore", $"Tags were restored but the library refresh failed for {track.Path}.", ex);
                await ShowNoticeAsync("The file was restored, but the library could not refresh. Scan the library to update its tags.", InfoBarSeverity.Warning);
            }
            else
            {
                LocalAppLog.Shared.Error("tag-restore", $"Could not restore tags for {track.Path}.", ex);
                await ShowNoticeAsync($"Could not restore the backup: {ex.Message}", InfoBarSeverity.Error);
            }
        }
    }

    private async void Details_Click(object sender, RoutedEventArgs e)
    {
        var track = TrackFromSender(sender); if (track is null) return;
        try
        {
            var details = TrackInformation.Read(track.Path);
            var backup = _store.GetLatestTagBackup(track.Path);
            var customTags = TagEditor.ReadCustomFields(track.Path);
            var customText = customTags.Count == 0 ? "None" : string.Join(Environment.NewLine, customTags.Select(field => $"{field.Key}={field.Value}"));
            var text = $"Title: {track.Title}\nArtist: {track.Artist}\nAlbum: {track.Album}\nAlbum artist: {track.AlbumArtist}\nGenre: {track.Genre}\nYear: {track.Year}\nTrack: {track.TrackNumber}\n\nCustom tags\n{customText}\n\nPath\n{details.Path}\n\nContainer\n{details.Container}\n\nDuration\n{details.Duration}\n\nBitrate\n{details.BitrateKbps} kbps\n\nSample rate\n{details.SampleRateHz:N0} Hz\n\nBit depth\n{details.BitsPerSample} bit\n\nFile size\n{details.FileSize:N0} bytes\n\nModified\n{details.ModifiedUtc:u}\n\nBackup\n{backup?.BackupPath ?? "No tag backup"}";
            await ShowTextDialogAsync("Track details", text);
        }
        catch (Exception ex)
        { LocalAppLog.Shared.Error("track-details", $"Could not read details for {track.Path}.", ex); await ShowNoticeAsync($"Could not read track details: {ex.Message}", InfoBarSeverity.Error); }
    }

    private async void Lyrics_Click(object sender, RoutedEventArgs e)
    {
        var track = TrackFromSender(sender); if (track is not null) await EditLyricsAsync(track);
    }

    private async void OpenLyrics_Click(object sender, RoutedEventArgs e)
    {
        if (_playback.CurrentTrack is { } track) await EditLyricsAsync(track);
    }

    private async Task EditLyricsAsync(Track track)
    {
        var rawLyrics = LyricsFiles.ReadRaw(track.Path);
        var editor = new TextBox { Text = rawLyrics, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 260, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe UI") };
        var mode = new ComboBox { Header = "Save lyrics as", SelectedIndex = 0 };
        mode.Items.Add("Sidecar .lrc file"); mode.Items.Add("Embed in audio tags (creates backup)");
        var offset = new TextBox { Header = "Timing offset (milliseconds)", Text = ((int)Lyrics.Parse(rawLyrics).Offset.TotalMilliseconds).ToString() };
        var stamp = new Button { Content = "Stamp current playback time at the caret", HorizontalAlignment = HorizontalAlignment.Left };
        stamp.Click += (_, _) => StampLyricLine(editor);
        var search = new Button { Content = "Search LRCLIB…", HorizontalAlignment = HorizontalAlignment.Left };
        var results = new ComboBox { MinWidth = 360, Visibility = Visibility.Collapsed };
        var useResult = new Button { Content = "Use selected lyrics", HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
        var searchStatus = new TextBlock { Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], TextWrapping = TextWrapping.Wrap };
        useResult.Click += (_, _) =>
        {
            if (results.SelectedItem is LyricsSearchResult result)
            {
                editor.Text = result.SyncedLyrics ?? result.PlainLyrics ?? string.Empty;
                searchStatus.Text = $"Loaded lyrics from {result.ArtistName} — {result.TrackName}.";
            }
        };
        search.Click += async (_, _) =>
        {
            search.IsEnabled = false;
            searchStatus.Text = "Searching LRCLIB…";
            try
            {
                var matches = await LyricsFiles.SearchLrclibAsync(track.Title, track.Artist);
                results.ItemsSource = matches;
                results.DisplayMemberPath = "TrackName";
                results.SelectedIndex = matches.Count > 0 ? 0 : -1;
                results.Visibility = matches.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                useResult.Visibility = matches.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                searchStatus.Text = matches.Count == 0 ? "LRCLIB found no matching lyrics." : $"Found {matches.Count} result(s). Choose one, then load it into the editor.";
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            { LocalAppLog.Shared.Warning("lyrics-search", "User-requested LRCLIB search failed.", ex); searchStatus.Text = $"LRCLIB search failed: {ex.Message}"; }
            catch (Exception ex)
            { LocalAppLog.Shared.Error("lyrics-search", "Unexpected failure during user-requested LRCLIB search.", ex); searchStatus.Text = "LRCLIB search failed. See the local log for details."; }
            finally { search.IsEnabled = true; }
        };
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Text = track.Title + " · " + track.Artist, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
        body.Children.Add(editor); body.Children.Add(offset); body.Children.Add(mode); body.Children.Add(stamp); body.Children.Add(search); body.Children.Add(results); body.Children.Add(useResult); body.Children.Add(searchStatus);
        var dialog = new ContentDialog { Title = "Lyrics and timing", Content = new ScrollViewer { Content = body, MaxHeight = 620 }, PrimaryButtonText = "Save lyrics", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = ShellRoot.XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (!int.TryParse(offset.Text, out var offsetMs)) { await ShowNoticeAsync("Timing offset must be a whole number of milliseconds.", InfoBarSeverity.Warning); return; }
        var lyricsText = SetLyricsOffset(editor.Text, offsetMs);
        TagBackup? backup = null;
        var lyricsWritten = false;
        try
        {
            if (mode.SelectedIndex == 0) LyricsFiles.SaveSidecar(track.Path, lyricsText);
            else
            {
                backup = new TagEditor(Path.Combine(_appData, "TagBackups")).Save(track.Path, new TagEdit(Lyrics: lyricsText));
            }
            lyricsWritten = true;
            _currentLyrics = Lyrics.Parse(lyricsText);
            var currentLyrics = Lyrics.DisplayAt(_currentLyrics, lyricsText, TimeSpan.FromMilliseconds(_playback.Position));
            NowPlayingLyrics.Text = currentLyrics.Length == 0 && _currentLyrics.Lines.Count == 0 ? "Lyrics saved." : currentLyrics;
            if (backup is not null)
            {
                _store.RecordTagBackup(backup); _store.UpsertTrack(TrackReader.Read(track.Path, Path.Combine(_appData, "Artwork")));
            }
        }
        catch (Exception ex)
        {
            if (lyricsWritten)
            {
                LocalAppLog.Shared.Warning("lyrics-editor", $"Lyrics were saved but the library refresh failed for {track.Path}. Backup: {backup?.BackupPath}", ex);
                await ShowNoticeAsync($"Lyrics were saved, but the library could not refresh. Backup: {backup?.BackupPath ?? LyricsFiles.SidecarPath(track.Path)}", InfoBarSeverity.Warning);
            }
            else
            {
                LocalAppLog.Shared.Error("lyrics-editor", $"Could not save lyrics for {track.Path}.", ex);
                await ShowNoticeAsync($"Lyrics were not saved. The original file is intact. {ex.Message}", InfoBarSeverity.Error);
            }
        }
    }

    private void StampLyricLine(TextBox editor)
    {
        var position = Math.Max(0, _playback.Position); var cursor = Math.Clamp(editor.SelectionStart, 0, editor.Text.Length);
        var text = editor.Text; var lineStart = text.LastIndexOf('\n', Math.Max(0, cursor - 1)); lineStart++;
        var time = TimeSpan.FromMilliseconds(position); var stamp = $"[{(int)time.TotalMinutes:00}:{time.Seconds:00}.{time.Milliseconds / 10:00}]";
        if (!text.AsSpan(lineStart).StartsWith("[", StringComparison.Ordinal)) editor.Text = text.Insert(lineStart, stamp);
        editor.SelectionStart = lineStart + stamp.Length;
    }

    private static string SetLyricsOffset(string text, int milliseconds)
    {
        var lines = text.Split('\n').Where(line => !line.Trim().StartsWith("[offset:", StringComparison.OrdinalIgnoreCase)).ToList();
        if (milliseconds != 0) lines.Insert(0, $"[offset:{milliseconds}]");
        return string.Join('\n', lines);
    }

    private async void Queue_Click(object sender, RoutedEventArgs e) => await ShowQueueAsync();

    private async Task ShowQueueAsync()
    {
        var queue = new ListView { Height = 360, SelectionMode = ListViewSelectionMode.Single };
        var body = new StackPanel { Spacing = 8, MinWidth = 480 };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var up = new Button { Content = "Move up" }; var down = new Button { Content = "Move down" };
        var remove = new Button { Content = "Remove" }; var clear = new Button { Content = "Clear upcoming" };
        var queueStatus = new TextBlock { Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], TextWrapping = TextWrapping.Wrap };
        void RefreshQueue()
        {
            queue.ItemsSource = _queue.Select((track, index) => $"{(index == _queueIndex ? "Playing · " : "")}{index + 1}. {track.Title} — {track.Artist}").ToArray();
            if (_queueIndex >= 0 && _queueIndex < queue.Items.Count) queue.SelectedIndex = _queueIndex;
        }
        void Move(int direction)
        {
            var index = queue.SelectedIndex; var next = index + direction;
            if (index < 0 || next < 0 || next >= _queue.Count) return;
            (_queue[index], _queue[next]) = (_queue[next], _queue[index]);
            if (_queueIndex == index) _queueIndex = next; else if (_queueIndex == next) _queueIndex = index;
            RefreshQueue(); queue.SelectedIndex = next; SaveSession();
        }
        up.Click += (_, _) => Move(-1); down.Click += (_, _) => Move(1);
        remove.Click += async (_, _) =>
        {
            var index = queue.SelectedIndex;
            if (index < 0 || index >= _queue.Count) return;
            if (index == _queueIndex) { queueStatus.Text = "The currently playing track cannot be removed from the queue."; return; }
            _queue.RemoveAt(index); if (index < _queueIndex) _queueIndex--;
            RefreshQueue(); SaveSession();
        };
        clear.Click += (_, _) =>
        {
            var current = _playback.CurrentTrack;
            _queue.Clear();
            if (current is not null) { _queue.Add(current); _queueIndex = 0; } else _queueIndex = -1;
            RefreshQueue(); SaveSession();
        };
        queue.DoubleTapped += (_, _) =>
        {
            var index = queue.SelectedIndex;
            if (index >= 0 && index < _queue.Count) PlayTrack(_queue[index], false, index);
            RefreshQueue();
        };
        controls.Children.Add(up); controls.Children.Add(down); controls.Children.Add(remove); controls.Children.Add(clear);
        body.Children.Add(queue); body.Children.Add(controls); body.Children.Add(queueStatus); RefreshQueue();
        var dialog = new ContentDialog { Title = "Playback queue", Content = body, CloseButtonText = "Done", XamlRoot = ShellRoot.XamlRoot };
        await dialog.ShowAsync();
    }

    private async void Equalizer_Click(object sender, RoutedEventArgs e)
    {
        var presets = PlaybackService.EqualizerPresets();
        var saved = new Dictionary<string, string>(_store.GetSettings("eq-preset:"), StringComparer.OrdinalIgnoreCase);
        var presetBox = new ComboBox { Header = "Preset", MinWidth = 220 };
        presetBox.Items.Add("Off"); foreach (var preset in presets) presetBox.Items.Add(preset);
        foreach (var item in saved.Keys) presetBox.Items.Add(item["eq-preset:".Length..]);
        var currentPreset = _store.GetSetting("eq-current") ?? "Off";
        presetBox.SelectedItem = presetBox.Items.Cast<object>().FirstOrDefault(item => item.ToString() == currentPreset) ?? "Off";
        var labels = PlaybackService.EqualizerBands();
        var sliders = new List<Slider>(); var controls = new StackPanel { Spacing = 2 };
        var customJson = _store.GetSetting("eq-bands");
        var values = ReadEqualizerBands(customJson, labels.Count);
        for (var i = 0; i < labels.Count; i++)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
            var label = new TextBlock { Text = labels[i], VerticalAlignment = VerticalAlignment.Center };
            var slider = new Slider { Minimum = -20, Maximum = 20, Value = values[i], Tag = i };
            var value = new TextBlock { Text = slider.Value.ToString("0.0"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            slider.ValueChanged += (_, args) =>
            {
                value.Text = args.NewValue.ToString("0.0");
                if (presetBox.SelectedItem?.ToString() == "Off") return;
                var bands = sliders.Select(control => (float)control.Value).ToArray(); _playback.ApplyEqualizer(null, bands);
                _store.SetSetting("eq-current", "Custom"); _store.SetSetting("eq-bands", JsonSerializer.Serialize(bands));
            };
            sliders.Add(slider); Grid.SetColumn(slider, 1); Grid.SetColumn(value, 2); row.Children.Add(label); row.Children.Add(slider); row.Children.Add(value); controls.Children.Add(row);
        }
        var saveName = new TextBox { Header = "Save current custom preset as" };
        var savePreset = new Button { Content = "Save preset", HorizontalAlignment = HorizontalAlignment.Left };
        var presetStatus = new TextBlock { Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], TextWrapping = TextWrapping.Wrap };
        savePreset.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(saveName.Text)) { presetStatus.Text = "Enter a preset name first."; return; }
            var name = saveName.Text.Trim();
            if (name.Equals("Off", StringComparison.OrdinalIgnoreCase) || presets.Contains(name, StringComparer.OrdinalIgnoreCase))
            { presetStatus.Text = "Choose a name that is different from the built-in presets."; return; }
            var bands = sliders.Select(control => (float)control.Value).ToArray();
            var requestedKey = "eq-preset:" + name;
            var key = saved.Keys.FirstOrDefault(candidate => candidate.Equals(requestedKey, StringComparison.OrdinalIgnoreCase)) ?? requestedKey;
            var displayName = key["eq-preset:".Length..];
            var json = JsonSerializer.Serialize(bands);
            saved[key] = json;
            _store.SetSetting(key, json); _store.SetSetting("eq-current", displayName);
            if (!presetBox.Items.Cast<object>().Any(item => item.ToString()?.Equals(displayName, StringComparison.OrdinalIgnoreCase) == true)) presetBox.Items.Add(displayName);
            _playback.ApplyEqualizer(null, bands); presetBox.SelectedItem = displayName;
            presetStatus.Text = "Equalizer preset saved and applied.";
        };
        presetBox.SelectionChanged += (_, _) =>
        {
            if (presetBox.SelectedItem is not string selected) return;
            if (selected == "Off") { _playback.ApplyEqualizer(null); _store.SetSetting("eq-current", "Off"); return; }
            IReadOnlyList<float> bands;
            if (saved.TryGetValue("eq-preset:" + selected, out var json)) bands = ReadEqualizerBands(json, sliders.Count);
            else bands = PlaybackService.EqualizerPresetBands(selected);
            for (var i = 0; i < sliders.Count && i < bands.Count; i++) sliders[i].Value = bands[i];
            if (saved.ContainsKey("eq-preset:" + selected)) _playback.ApplyEqualizer(null, bands);
            else _playback.ApplyEqualizer(selected);
            _store.SetSetting("eq-current", selected);
        };
        var content = new StackPanel { Spacing = 8 }; content.Children.Add(presetBox); content.Children.Add(controls); content.Children.Add(saveName); content.Children.Add(savePreset); content.Children.Add(presetStatus);
        var dialog = new ContentDialog { Title = "10-band equalizer", Content = new ScrollViewer { Content = content, MaxHeight = 650 }, CloseButtonText = "Done", XamlRoot = ShellRoot.XamlRoot };
        await dialog.ShowAsync();
    }

    private async Task ShowTextDialogAsync(string title, string text)
    {
        var body = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        var dialog = new ContentDialog { Title = title, Content = new ScrollViewer { Content = body, MaxHeight = 560 }, CloseButtonText = "Close", XamlRoot = ShellRoot.XamlRoot };
        await dialog.ShowAsync();
    }

    private void InitializeSystemMediaControls()
    {
        try
        {
            _systemControls = SystemMediaTransportControlsInterop.GetForWindow(WindowNative.GetWindowHandle(this));
            _systemControls.IsEnabled = true; _systemControls.IsPlayEnabled = true; _systemControls.IsPauseEnabled = true;
            _systemControls.IsNextEnabled = true; _systemControls.IsPreviousEnabled = true;
            _systemControls.ButtonPressed += SystemControls_ButtonPressed;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { LocalAppLog.Shared.Warning("system-media-controls", "Could not initialize Windows media controls.", ex); }
    }

    private void SystemControls_ButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            switch (args.Button)
            {
                case SystemMediaTransportControlsButton.Play:
                    if (_playback.CurrentTrack is null && _tracks.Count > 0) PlayTrack(_tracks[0], true);
                    else { _lastPlayCountPosition = _playback.Position; _playback.PlayLoaded(); PlayPauseButton.Content = "Pause"; }
                    if (_systemControls is not null) _systemControls.PlaybackStatus = MediaPlaybackStatus.Playing;
                    break;
                case SystemMediaTransportControlsButton.Pause: _playback.Pause(); _crossfadeInProgress = false; PlayPauseButton.Content = "Play"; if (_systemControls is not null) _systemControls.PlaybackStatus = MediaPlaybackStatus.Paused; break;
                case SystemMediaTransportControlsButton.Next: AdvanceQueue(false); break;
                case SystemMediaTransportControlsButton.Previous: Previous_Click(this, new RoutedEventArgs()); break;
            }
        });
    }

    private void UpdateSystemMediaControls(Track? track, bool playing)
    {
        if (_systemControls is null || track is null) return;
        _systemControls.IsEnabled = true;
        _systemControls.PlaybackStatus = playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
        var updater = _systemControls.DisplayUpdater; updater.Type = MediaPlaybackType.Music;
        updater.MusicProperties.Title = track.Title; updater.MusicProperties.Artist = track.Artist; updater.MusicProperties.AlbumTitle = track.Album;
        updater.Thumbnail = null;
        updater.Update();
        if (!string.IsNullOrWhiteSpace(track.ArtworkPath) && System.IO.File.Exists(track.ArtworkPath)) _ = UpdateMediaThumbnailAsync(track.ArtworkPath);
    }

    private async Task UpdateMediaThumbnailAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            if (_systemControls is null || _playback.CurrentTrack?.ArtworkPath != path) return;
            var updater = _systemControls.DisplayUpdater;
            updater.Thumbnail = RandomAccessStreamReference.CreateFromFile(file);
            updater.Update();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        { LocalAppLog.Shared.Warning("media-artwork", $"Could not load artwork: {path}", ex); }
    }

    private void ApplyTraySetting()
    {
        if (_store.GetSetting("minimize-to-tray") == "true")
        {
            _tray ??= new TrayIconService(WindowNative.GetWindowHandle(this), Path.Combine(AppContext.BaseDirectory, "Assets", "BrackenVale.ico"),
                () => DispatcherQueue.TryEnqueue(() => { var hwnd = WindowNative.GetWindowHandle(this); TrayIconService.RestoreWindow(hwnd); Activate(); }));
        }
        else { _tray?.Dispose(); _tray = null; }
    }

    private Task ShowNoticeAsync(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        AppNotice.Message = message;
        AppNotice.Severity = severity;
        AppNotice.IsOpen = true;
        return Task.CompletedTask;
    }
}
