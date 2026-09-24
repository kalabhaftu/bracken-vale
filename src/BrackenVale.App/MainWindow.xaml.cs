using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using BrackenVale.Core;
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
            ScanStatusText.Text = $"Indexed {result.Indexed:N0} · skipped {result.Skipped:N0}";
        }
        catch (OperationCanceledException) { ScanStatusText.Text = "Scan cancelled; completed tracks are saved."; }
        catch (Exception ex) { ScanStatusText.Text = $"Scan stopped: {ex.Message}"; }
        finally
        {
            control.Dispose(); _scanControl = null; ScanPauseButton.Visibility = Visibility.Collapsed;
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
            source = _store.GetTracks(search, _sort, _descending, filter);
        }
        _tracks.Clear(); foreach (var track in source) _tracks.Add(track);
        TrackCountText.Text = $"{_tracks.Count:N0} {(_tracks.Count == 1 ? "track" : "tracks")}";
        EmptyState.Visibility = _tracks.Count == 0 && _selectedPlaylist is null ? Visibility.Visible : Visibility.Collapsed;
        PlaylistView.Visibility = _view == "Playlists" && _selectedPlaylist is null ? Visibility.Visible : Visibility.Collapsed;
        LibraryView.Visibility = _view != "Now Playing" && PlaylistView.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        NowPlayingView.Visibility = _view == "Now Playing" ? Visibility.Visible : Visibility.Collapsed;
        if (_selectedPlaylist is not null) ViewTitle.Text = _selectedPlaylist.Name;
        else ViewTitle.Text = _view;
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
        _sort = next switch { "Albums" => TrackSort.Album, "Artists" => TrackSort.Artist, "Genres" => TrackSort.Genre, "Folders" => TrackSort.Path,
            "Recently Added" => TrackSort.Added, "Most Played" => TrackSort.PlayCount, "Recently Played" => TrackSort.LastPlayed, _ => TrackSort.Title };
        _descending = next is "Recently Added" or "Most Played" or "Recently Played";
        if (SortBox is not null) SortBox.SelectedIndex = _sort switch
        {
            TrackSort.Artist => 1, TrackSort.Album => 2, TrackSort.Genre => 3, TrackSort.Year => 4,
            TrackSort.Added => 5, TrackSort.Duration => 6, TrackSort.PlayCount => 7, _ => 0
        };
        SortDirectionButton.Content = _descending ? "Descending" : "Ascending";
        RefreshLibrary();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshLibrary();

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SortBox.SelectedIndex >= 0 && SortBox.SelectedIndex < 8)
        {
            _sort = SortBox.SelectedIndex switch { 0 => TrackSort.Title, 1 => TrackSort.Artist, 2 => TrackSort.Album, 3 => TrackSort.Genre,
                4 => TrackSort.Year, 5 => TrackSort.Added, 6 => TrackSort.Duration, _ => TrackSort.PlayCount };
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

    private void PlayTrack(Track track, bool resetQueue)
    {
        if (resetQueue)
        {
            _queue.Clear(); _queue.AddRange(_tracks);
            _queueIndex = _queue.FindIndex(item => item.Path.Equals(track.Path, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            var index = _queue.FindIndex(item => item.Path.Equals(track.Path, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) _queueIndex = index;
        }
        _playback.Play(track); _countedCurrentPlay = false; _heardMilliseconds = 0; _lastPlayCountPosition = 0; _crossfadeInProgress = false; _repeatA = _repeatB = null;
        UpdateCurrentTrack(track); UpdateSystemMediaControls(track, true); PlayPauseButton.Content = "Pause"; SeekSlider.IsEnabled = true; SaveSession();
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_playback.CurrentTrack is null)
        {
            if (_tracks.Count > 0) PlayTrack(_tracks[0], true);
            return;
        }
        if (_playback.IsPlaying) { _playback.Pause(); if (_systemControls is not null) _systemControls.PlaybackStatus = MediaPlaybackStatus.Paused; PlayPauseButton.Content = "Play"; }
        else { _lastPlayCountPosition = _playback.Position; _playback.PlayLoaded(); UpdateSystemMediaControls(_playback.CurrentTrack, true); PlayPauseButton.Content = "Pause"; }
    }

    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (_playback.Position > 3000) { _playback.Seek(0); return; }
        if (_queueIndex > 0) { _queueIndex--; PlayTrack(_queue[_queueIndex], false); }
    }

    private void Next_Click(object sender, RoutedEventArgs e) => AdvanceQueue(false);

    private void AdvanceQueue(bool automatic)
    {
        if (_queue.Count == 0) { if (_tracks.Count > 0) { _queue.AddRange(_tracks); _queueIndex = -1; } }
        if (_queue.Count == 0) return;
        if (automatic && _repeatMode == "Track" && _queueIndex >= 0) { PlayTrack(_queue[_queueIndex], false); return; }
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
        if (_playback.IsPlaying && int.TryParse(_store.GetSetting("crossfade-seconds"), out var seconds) && seconds > 0)
        {
            _queueIndex = next; _crossfadeInProgress = true;
            _ = _playback.CrossfadeToAsync(track, seconds * 1000);
        }
        else PlayTrack(track, false);
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
        if (_playback.IsPlaying && !_crossfadeInProgress && _queueIndex + 1 < _queue.Count &&
            int.TryParse(_store.GetSetting("crossfade-seconds"), out var crossfade) && crossfade > 0 && duration > 0 && duration - position <= crossfade * 1000)
        {
            _queueIndex++; _crossfadeInProgress = true; _ = _playback.CrossfadeToAsync(_queue[_queueIndex], crossfade * 1000);
        }
        if (DateTime.UtcNow - _lastSessionSave > TimeSpan.FromSeconds(5)) SaveSession();
        if (_playback.CurrentTrack is not null && _currentLyrics.Lines.Count > 0)
        {
            var line = _currentLyrics.At(TimeSpan.FromMilliseconds(position));
            if (!string.IsNullOrEmpty(line)) NowPlayingLyrics.Text = line;
        }
    }

    private static string FormatTime(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(milliseconds);
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    private void Playback_TrackEnded(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() => AdvanceQueue(true));
    private void Playback_CrossfadeCompleted(Track track) => DispatcherQueue.TryEnqueue(() =>
    {
        _crossfadeInProgress = false; _countedCurrentPlay = false; _heardMilliseconds = 0; _lastPlayCountPosition = 0;
        UpdateCurrentTrack(track); UpdateSystemMediaControls(track, true); PlayPauseButton.Content = "Pause";
    });

    private void UpdateCurrentTrack(Track track)
    {
        PlayerTitle.Text = track.Title; PlayerArtist.Text = track.Artist;
        NowPlayingTitle.Text = track.Title; NowPlayingArtist.Text = track.Artist;
        _currentLyrics = LyricsFiles.Load(track.Path);
        NowPlayingLyrics.Text = _currentLyrics.Lines.FirstOrDefault()?.Text ?? "No synced lyrics. Open lyrics to add or search.";
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
            try
            {
                var bands = JsonSerializer.Deserialize<float[]>(saved);
                if (bands is { Length: > 0 })
                {
                    _playback.ApplyEqualizer(null, bands.Select(band => float.IsFinite(band) ? Math.Clamp(band, -20, 20) : 0).ToArray());
                    return;
                }
            }
            catch (JsonException) { }
        }
        if (PlaybackService.EqualizerPresets().Contains(selected, StringComparer.OrdinalIgnoreCase)) _playback.ApplyEqualizer(selected);
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
        catch (Exception ex) { Debug.WriteLine($"Could not save the playback session: {ex.Message}"); }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        SaveSession(); _clock.Stop(); _playback.Dispose(); _scanControl?.Cancel();
        _tray?.Dispose();
    }

    private void ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = (sender as FrameworkElement)?.Tag?.ToString(); if (path is null) return;
        var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = true }; info.ArgumentList.Add("/select," + path); Process.Start(info);
    }

    private Track? TrackFromSender(object sender)
    {
        var path = (sender as FrameworkElement)?.Tag?.ToString(); return path is null ? null : _store.GetTrack(path);
    }

    private T[] ReadJsonSetting<T>(string key, T[] fallback)
    {
        try { return _store.GetSetting(key) is { } json ? JsonSerializer.Deserialize<T[]>(json) ?? fallback : fallback; }
        catch (JsonException) { return fallback; }
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
        var updateNow = new Button { Content = "Check for updates now" }; updateNow.Click += CheckUpdates_Click; content.Children.Add(updateNow);
        content.Children.Add(manualAccent);
        content.Children.Add(new TextBlock { Text = "Custom accent", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
        content.Children.Add(colorPicker); content.Children.Add(crossfade); content.Children.Add(ignored);
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
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
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
        { await ShowNoticeAsync($"One ignored folder path is invalid: {ex.Message}"); return; }
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
        RefreshLibrary();
        if (_store.GetSetting("check-updates") == "true") _ = CheckForUpdatesAsync(true);
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
            using var stream = await StorageFile.GetFileFromPath(artworkPath).OpenAsync(FileAccessMode.Read);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Rgba8, BitmapAlphaMode.Ignore,
                new BitmapTransform { ScaledWidth = 1, ScaledHeight = 1 }, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            var pixels = data.DetachPixelData();
            if (pixels.Length < 3) { ResetAccent(); return false; }
            ApplyAccent(Color.FromArgb(255, pixels[0], pixels[1], pixels[2]));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        { ResetAccent(); return false; }
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

    private async Task CheckForUpdatesAsync(bool force)
    {
        if (!force && _store.GetSetting("check-updates") == "false") return;
        if (!force && DateTime.TryParse(_store.GetSetting("last-update-check"), out var last) && DateTime.UtcNow - last.ToUniversalTime() < TimeSpan.FromDays(7)) return;
        try
        {
            var release = await GitHubUpdates.GetLatestAsync();
            _store.SetSetting("last-update-check", DateTime.UtcNow.ToString("O"));
            var current = typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 1, 0);
            if (release is null || !Version.TryParse(release.Tag.TrimStart('v'), out var latest) || latest <= current)
            { if (force) await ShowNoticeAsync("You are using the latest available release."); return; }
            ReleaseNotice.Title = $"Bracken Vale {release.Tag} is available";
            ReleaseNotice.Message = "A new release is ready to view. Updates are never downloaded automatically.";
            var open = new Button { Content = "View release" }; open.Click += (_, _) => Process.Start(new ProcessStartInfo(release.Url) { UseShellExecute = true });
            ReleaseNotice.ActionButton = open; ReleaseNotice.IsOpen = true;
        }
        catch (HttpRequestException) { if (force) await ShowNoticeAsync("Could not reach GitHub. Check your connection and try again."); }
        catch (TaskCanceledException) { if (force) await ShowNoticeAsync("The GitHub update check timed out."); }
        catch (System.Text.Json.JsonException) { if (force) await ShowNoticeAsync("GitHub returned an update response Bracken Vale could not read."); }
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
        { await ShowNoticeAsync($"Could not import that playlist: {ex.Message}"); }
    }

    private async void ExportPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPlaylist is null) { await ShowNoticeAsync("Select a playlist first."); return; }
        var picker = new FileSavePicker(); picker.FileTypeChoices.Add("M3U8 playlist", [".m3u8"]); picker.SuggestedFileName = _selectedPlaylist.Name;
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync(); if (file is null) return;
        try { _store.ExportM3u8(_selectedPlaylist.Id, file.Path); await ShowNoticeAsync("Playlist exported as UTF-8 M3U8."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { await ShowNoticeAsync($"Could not export that playlist: {ex.Message}"); }
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
        var track = TrackFromSender(sender); if (track is null) return;
        var position = Array.FindIndex(_selectedPlaylist.Paths.ToArray(), path => path.Equals(track.Path, StringComparison.OrdinalIgnoreCase));
        if (position < 0) return;
        _store.RemoveFromPlaylist(_selectedPlaylist.Id, position);
        _selectedPlaylist = _store.GetPlaylists().FirstOrDefault(item => item.Id == _selectedPlaylist.Id);
        RefreshLibrary(); RefreshPlaylists();
    }

    private async void EditTags_Click(object sender, RoutedEventArgs e)
    {
        if (TrackFromSender(sender) is not { } track) return;
        var title = AddTextField("Title", track.Title); var artist = AddTextField("Artist", track.Artist);
        var album = AddTextField("Album", track.Album); var albumArtist = AddTextField("Album artist", track.AlbumArtist); var genre = AddTextField("Genre", track.Genre);
        var year = AddTextField("Year", track.Year == 0 ? "" : track.Year.ToString()); var number = AddTextField("Track number", track.TrackNumber == 0 ? "" : track.TrackNumber.ToString());
        var artwork = AddTextField("Artwork file (optional)", "");
        var artworkPreview = new Image { Width = 96, Height = 96, Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill };
        SetArtwork(artworkPreview, track.ArtworkPath);
        var custom = new TextBox { Header = "Format-specific Xiph fields (KEY=value, one per line)", AcceptsReturn = true, MinHeight = 70, TextWrapping = TextWrapping.Wrap };
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
        try
        {
            var customFields = ParseCustomFields(custom.Text);
            var edit = new TagEdit(title.Text, artist.Text, album.Text, albumArtist.Text, genre.Text,
                uint.TryParse(year.Text, out var parsedYear) ? parsedYear : null,
                uint.TryParse(number.Text, out var parsedNumber) ? parsedNumber : null,
                ArtworkPath: string.IsNullOrWhiteSpace(artwork.Text) ? null : artwork.Text,
                CustomFields: customFields);
            var backup = new TagEditor(Path.Combine(_appData, "TagBackups")).Save(track.Path, edit);
            _store.RecordTagBackup(backup);
            _store.UpsertTrack(TrackReader.Read(track.Path, Path.Combine(_appData, "Artwork")));
            RefreshLibrary();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TagLib.CorruptFileException or TagLib.UnsupportedFormatException or NotSupportedException or ArgumentException)
        { await ShowNoticeAsync($"Tags were not saved. The original file is intact. {ex.Message}"); }
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
        try
        {
            new TagEditor(Path.Combine(_appData, "TagBackups")).Restore(backup);
            _store.UpsertTrack(TrackReader.Read(track.Path, Path.Combine(_appData, "Artwork"))); RefreshLibrary();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { await ShowNoticeAsync($"Could not restore the backup: {ex.Message}"); }
    }

    private async void Details_Click(object sender, RoutedEventArgs e)
    {
        var track = TrackFromSender(sender); if (track is null) return;
        try
        {
            var details = TrackInformation.Read(track.Path);
            var backup = _store.GetLatestTagBackup(track.Path);
            var text = $"Title: {track.Title}\nArtist: {track.Artist}\nAlbum: {track.Album}\nAlbum artist: {track.AlbumArtist}\nGenre: {track.Genre}\nYear: {track.Year}\nTrack: {track.TrackNumber}\n\nPath\n{details.Path}\n\nContainer\n{details.Container}\n\nDuration\n{details.Duration}\n\nBitrate\n{details.BitrateKbps} kbps\n\nSample rate\n{details.SampleRateHz:N0} Hz\n\nBit depth\n{details.BitsPerSample} bit\n\nFile size\n{details.FileSize:N0} bytes\n\nModified\n{details.ModifiedUtc:u}\n\nBackup\n{backup?.BackupPath ?? "No tag backup"}";
            await ShowTextDialogAsync("Track details", text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TagLib.CorruptFileException or TagLib.UnsupportedFormatException or NotSupportedException)
        { await ShowNoticeAsync($"Could not read track details: {ex.Message}"); }
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
        search.Click += async (_, _) =>
        {
            try
            {
                var matches = await LyricsFiles.SearchLrclibAsync(track.Title, track.Artist);
                if (matches.Count == 0) { await ShowNoticeAsync("LRCLIB found no matching lyrics."); return; }
                var picker = new ComboBox { ItemsSource = matches, DisplayMemberPath = "TrackName", SelectedIndex = 0, MinWidth = 360 };
                var choose = new ContentDialog { Title = "Choose lyrics", Content = picker, PrimaryButtonText = "Use selected lyrics", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = ShellRoot.XamlRoot };
                if (await choose.ShowAsync() == ContentDialogResult.Primary && picker.SelectedItem is LyricsSearchResult result)
                    editor.Text = result.SyncedLyrics ?? result.PlainLyrics ?? string.Empty;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            { await ShowNoticeAsync($"LRCLIB search failed: {ex.Message}"); }
        };
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Text = track.Title + " · " + track.Artist, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
        body.Children.Add(editor); body.Children.Add(offset); body.Children.Add(mode); body.Children.Add(stamp); body.Children.Add(search);
        var dialog = new ContentDialog { Title = "Lyrics and timing", Content = new ScrollViewer { Content = body, MaxHeight = 620 }, PrimaryButtonText = "Save lyrics", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = ShellRoot.XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (!int.TryParse(offset.Text, out var offsetMs)) { await ShowNoticeAsync("Timing offset must be a whole number of milliseconds."); return; }
        var lyricsText = SetLyricsOffset(editor.Text, offsetMs);
        try
        {
            if (mode.SelectedIndex == 0) LyricsFiles.SaveSidecar(track.Path, lyricsText);
            else
            {
                var backup = new TagEditor(Path.Combine(_appData, "TagBackups")).Save(track.Path, new TagEdit(Lyrics: lyricsText));
                _store.RecordTagBackup(backup); _store.UpsertTrack(TrackReader.Read(track.Path, Path.Combine(_appData, "Artwork")));
            }
            _currentLyrics = Lyrics.Parse(lyricsText); NowPlayingLyrics.Text = _currentLyrics.Lines.FirstOrDefault()?.Text ?? "Lyrics saved.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TagLib.CorruptFileException or TagLib.UnsupportedFormatException or NotSupportedException or ArgumentException)
        { await ShowNoticeAsync($"Lyrics were not saved. The original file is intact. {ex.Message}"); }
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

    private async void Equalizer_Click(object sender, RoutedEventArgs e)
    {
        var presets = PlaybackService.EqualizerPresets();
        var saved = _store.GetSettings("eq-preset:");
        var presetBox = new ComboBox { Header = "Preset", MinWidth = 220 };
        presetBox.Items.Add("Off"); foreach (var preset in presets) presetBox.Items.Add(preset);
        foreach (var item in saved.Keys) presetBox.Items.Add(item["eq-preset:".Length..]);
        var currentPreset = _store.GetSetting("eq-current") ?? "Off";
        presetBox.SelectedItem = presetBox.Items.Cast<object>().FirstOrDefault(item => item.ToString() == currentPreset) ?? "Off";
        var labels = PlaybackService.EqualizerBands();
        var sliders = new List<Slider>(); var controls = new StackPanel { Spacing = 2 };
        var customJson = _store.GetSetting("eq-bands");
        var values = customJson is null ? Enumerable.Repeat(0f, labels.Count).ToArray() : JsonSerializer.Deserialize<float[]>(customJson) ?? Enumerable.Repeat(0f, labels.Count).ToArray();
        for (var i = 0; i < labels.Count; i++)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
            var label = new TextBlock { Text = labels[i], VerticalAlignment = VerticalAlignment.Center };
            var slider = new Slider { Minimum = -20, Maximum = 20, Value = i < values.Length ? values[i] : 0, Tag = i };
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
        savePreset.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(saveName.Text)) { await ShowNoticeAsync("Enter a preset name first."); return; }
            var bands = sliders.Select(control => (float)control.Value).ToArray();
            _store.SetSetting("eq-preset:" + saveName.Text.Trim(), JsonSerializer.Serialize(bands));
            _store.SetSetting("eq-current", saveName.Text.Trim()); await ShowNoticeAsync("Equalizer preset saved.");
        };
        presetBox.SelectionChanged += (_, _) =>
        {
            if (presetBox.SelectedItem is not string selected) return;
            if (selected == "Off") { _playback.ApplyEqualizer(null); _store.SetSetting("eq-current", "Off"); return; }
            IReadOnlyList<float> bands;
            if (saved.TryGetValue("eq-preset:" + selected, out var json)) bands = JsonSerializer.Deserialize<float[]>(json) ?? [];
            else bands = PlaybackService.EqualizerPresetBands(selected);
            for (var i = 0; i < sliders.Count && i < bands.Count; i++) sliders[i].Value = bands[i];
            if (saved.ContainsKey("eq-preset:" + selected)) _playback.ApplyEqualizer(null, bands);
            else _playback.ApplyEqualizer(selected);
            _store.SetSetting("eq-current", selected);
        };
        var content = new StackPanel { Spacing = 8 }; content.Children.Add(presetBox); content.Children.Add(controls); content.Children.Add(saveName); content.Children.Add(savePreset);
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
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { Debug.WriteLine(ex.Message); }
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
                case SystemMediaTransportControlsButton.Pause: _playback.Pause(); PlayPauseButton.Content = "Play"; if (_systemControls is not null) _systemControls.PlaybackStatus = MediaPlaybackStatus.Paused; break;
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
        if (!string.IsNullOrWhiteSpace(track.ArtworkPath) && System.IO.File.Exists(track.ArtworkPath)) updater.Thumbnail = RandomAccessStreamReference.CreateFromFile(StorageFile.GetFileFromPath(track.ArtworkPath));
        updater.Update();
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

    private async Task ShowNoticeAsync(string message)
    {
        var dialog = new ContentDialog { Title = "Bracken Vale", Content = message, CloseButtonText = "OK", XamlRoot = ShellRoot.XamlRoot };
        await dialog.ShowAsync();
    }
}
