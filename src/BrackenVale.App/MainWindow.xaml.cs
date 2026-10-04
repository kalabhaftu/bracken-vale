using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using BrackenVale.Core;
using Microsoft.UI.Composition;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Hosting;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Devices;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace BrackenVale.App;

public sealed partial class MainWindow : Window
{
    private readonly LibraryStore _store = LibraryStore.InAppData();
    private readonly string _appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrackenVale");
    private readonly ObservableCollection<Track> _tracks = [];
    private readonly ObservableCollection<PlaylistTrackRow> _playlistRows = [];
    private readonly ObservableCollection<PlaylistSummary> _playlists = [];
    private readonly List<string> _queue = [];
    private const int LibraryPageSize = 200;
    private readonly PlaybackService _playback = new();
    private readonly ArtworkImageCache _artworkCache = new(64L * 1024 * 1024);
    private readonly UISettings _uiSettings = new();
    private SystemMediaTransportControls? _systemControls;
    private TrayIconService? _tray;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private ScanControl? _scanControl;
    private TrackSort _sort = TrackSort.Title;
    private bool _descending;
    private bool _shuffle;
    private bool _uiReady;
    private bool _windowClosed;
    private bool _updatingPosition;
    private bool _countedCurrentPlay;
    private DateTimeOffset? _scanStartedUtc;
    private DateTimeOffset _lastScanStatusUpdateUtc;
    private string _scanCurrentPath = "";
    private int _scanFilesFound;
    private int _scanDirectoriesVisited;
    private bool _scanPaused;
    private long _heardMilliseconds;
    private long _lastPlayCountPosition;
    private bool _crossfadeInProgress;
    private string? _crossfadeFailureSource;
    private int? _crossfadeSourceQueueIndex;
    private int _queueIndex = -1;
    private string _view = "Songs";
    private int _pageIndex;
    private int _totalTrackCount;
    private DispatcherQueueTimer? _searchDebounce;
    private CancellationTokenSource? _librarySearchCancellation;
    private long _librarySearchRevision;
    private DispatcherQueueTimer? _volumeSaveDebounce;
    private int? _pendingVolumeSetting;
    private int _persistedVolume;
    private int _crossfadeSeconds;
    private bool _suppressVolumePersistence;
    private string _repeatMode = "Off";
    private PlaylistSummary? _selectedPlaylist;
    private TimeSpan? _repeatA;
    private TimeSpan? _repeatB;
    private DateTime _lastSessionSave = DateTime.UtcNow;
    private LyricsDocument _currentLyrics = new([], TimeSpan.Zero);

    public MainWindow()
    {
        InitializeComponent();
        _uiReady = true;
        Title = "Bracken Vale";
        _volumeSaveDebounce = DispatcherQueue.CreateTimer();
        _volumeSaveDebounce.Interval = TimeSpan.FromMilliseconds(250);
        _volumeSaveDebounce.IsRepeating = false;
        _volumeSaveDebounce.Tick += (_, _) => PersistPendingVolumeSetting();
        var volume = int.TryParse(_store.GetSetting("volume"), out var savedVolume) ? Math.Clamp(savedVolume, 0, 100) : 75;
        _persistedVolume = volume;
        _crossfadeSeconds = ReadCrossfadeSeconds();
        _suppressVolumePersistence = true;
        VolumeSlider.Value = volume; _playback.Volume = volume;
        _suppressVolumePersistence = false;
        _playback.SelectAudioOutputDevice(_store.GetSetting("audio-output-device"));
        ApplyStoredEqualizer();
        TrackList.ItemsSource = _tracks;
        PlaylistTrackList.ItemsSource = _playlistRows;
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
        _searchDebounce = DispatcherQueue.CreateTimer();
        _searchDebounce.Interval = TimeSpan.FromMilliseconds(275);
        _searchDebounce.IsRepeating = false;
        _searchDebounce.Tick += (_, _) => _ = RefreshLibraryFromSearchAsync();
        Closed += MainWindow_Closed;
        ApplyStoredAppearance();
        RestoreSession();
        UpdatePlaybackModeControls();
        UpdateResponsiveLayout(ShellRoot.ActualWidth);
        RefreshLibrary();
        RefreshPlaylists();
        StartStartupScan();
        _ = CheckForUpdatesAsync(false);
    }

    private void StartStartupScan()
    {
        var roots = ReadJsonSetting("library-roots", Array.Empty<string>()).ToList();
        if (roots.Count == 0 && _store.GetSetting("library-roots-configured") != "true")
        {
            roots = LibraryScanner.DefaultRoots().ToList();
            _store.SetSetting("library-roots", JsonSerializer.Serialize(roots));
            _store.SetSetting("library-roots-configured", "true");
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
        _scanStartedUtc = DateTimeOffset.UtcNow;
        _lastScanStatusUpdateUtc = DateTimeOffset.MinValue;
        _scanCurrentPath = "Preparing scan…";
        _scanFilesFound = 0;
        _scanDirectoriesVisited = 0;
        _scanPaused = false;
        AddFolderButton.IsEnabled = ScanLibraryButton.IsEnabled = false;
        ManageRootsButton.IsEnabled = false;
        ScanStatus.Visibility = Visibility.Visible; ScanPauseButton.Visibility = Visibility.Visible; ScanPauseButton.Content = "Pause scan";
        ScanCancelButton.IsEnabled = true;
        ScanProgress.IsIndeterminate = true; UpdateScanStatusText();
        try
        {
            var ignored = ReadJsonSetting("ignored-directories", Array.Empty<string>());
            var indexer = new LibraryIndexer(_store, Path.Combine(_appData, "Artwork"));
            var nextLibraryRefresh = 256;
            var progress = new Progress<ScanProgress>(value =>
            {
                if (_windowClosed) return;
                _scanFilesFound = value.FilesFound;
                _scanDirectoriesVisited = value.DirectoriesVisited;
                if (!string.IsNullOrWhiteSpace(value.CurrentPath)) _scanCurrentPath = value.CurrentPath;
                UpdateScanStatusText();
                if (value.FilesFound >= nextLibraryRefresh) { RefreshLibrary(); nextLibraryRefresh = value.FilesFound + 256; }
            });
            var result = await Task.Run(() => indexer.ScanAsync(scanRoots, ignored, control, progress));
            if (!_windowClosed)
            {
                _scanCurrentPath = "Scan complete";
                ScanStatusText.Text = $"Scan complete · indexed {result.Indexed:N0} · removed {result.Removed:N0} · skipped {result.Skipped:N0} · {FormatElapsed(_scanStartedUtc)}";
                ToolTipService.SetToolTip(ScanStatusText, ScanStatusText.Text);
            }
        }
        catch (OperationCanceledException)
        {
            LocalAppLog.Shared.Info("scanner", "Library scan cancelled; completed tracks were retained.");
            if (!_windowClosed) { _scanCurrentPath = "Scan cancelled"; ScanStatusText.Text = $"Scan cancelled · saved completed tracks · {FormatElapsed(_scanStartedUtc)}"; }
        }
        catch (Exception ex)
        {
            LocalAppLog.Shared.Error("scanner", "Library scan failed.", ex);
            if (!_windowClosed) { _scanCurrentPath = "Scan stopped"; ScanStatusText.Text = $"Scan stopped: {ex.Message} · {FormatElapsed(_scanStartedUtc)}"; }
        }
        finally
        {
            control.Dispose();
            if (ReferenceEquals(_scanControl, control)) _scanControl = null;
            if (!_windowClosed)
            {
                AddFolderButton.IsEnabled = ScanLibraryButton.IsEnabled = ManageRootsButton.IsEnabled = true; ScanPauseButton.Visibility = Visibility.Collapsed;
                ConfigureGroupView();
                _ = Task.Delay(4500).ContinueWith(_ => DispatcherQueue.TryEnqueue(() =>
                {
                    if (!_windowClosed) ScanStatus.Visibility = Visibility.Collapsed;
                }));
                RefreshLibrary();
            }
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
        _store.SetSetting("library-roots-configured", "true");
        StartScan([folder.Path]);
    }

    private async void ManageRoots_Click(object sender, RoutedEventArgs e) => await ManageLibraryRootsAsync();

    private async Task ManageLibraryRootsAsync()
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var originalRoots = ReadJsonSetting("library-roots", Array.Empty<string>())
            .Select(NormalizeRoot).Where(path => path is not null).Cast<string>().Distinct(comparer).ToArray();
        var roots = new ObservableCollection<string>(originalRoots);
        var list = new ListView { ItemsSource = roots, SelectionMode = ListViewSelectionMode.Single, MinHeight = 160, MaxHeight = 320 };
        AutomationProperties.SetName(list, "Folders included in the music library");
        var add = new Button { Content = "Add folder…" };
        var remove = new Button { Content = "Remove selected", IsEnabled = false };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] };
        list.SelectionChanged += (_, _) => remove.IsEnabled = list.SelectedItem is string;
        add.Click += async (_, _) =>
        {
            var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            var fullPath = NormalizeRoot(folder.Path);
            if (fullPath is null) return;
            if (roots.Contains(fullPath, comparer)) { status.Text = "That folder is already included."; return; }
            roots.Add(fullPath); list.SelectedItem = fullPath; status.Text = "Folder added. Save to include it in the library.";
        };
        remove.Click += (_, _) =>
        {
            if (list.SelectedItem is not string selected) return;
            roots.Remove(selected);
            status.Text = "Saving will remove this folder from future scans and remove unshared indexed tracks. Music files are never deleted; playlist entries remain saved.";
        };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        controls.Children.Add(add); controls.Children.Add(remove);
        var body = new StackPanel { Spacing = 10, MinWidth = 420 };
        body.Children.Add(new TextBlock { Text = "Bracken Vale scans these locations for music. Removing a folder only changes the library index; it never deletes files.", TextWrapping = TextWrapping.Wrap });
        body.Children.Add(list); body.Children.Add(controls); body.Children.Add(status);
        var dialog = new ContentDialog
        {
            Title = "Library folders", Content = body, PrimaryButtonText = "Save", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary, XamlRoot = ShellRoot.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var remainingRoots = roots.ToArray();
        var removedRoots = originalRoots.Where(oldRoot => !remainingRoots.Contains(oldRoot, comparer)).ToArray();
        var addedRoots = remainingRoots.Where(newRoot => !originalRoots.Contains(newRoot, comparer)).ToArray();
        if (removedRoots.Length > 0)
        {
            var confirm = new ContentDialog
            {
                Title = "Remove folders from the library?",
                Content = $"Bracken Vale will stop scanning these folders and remove unshared tracks from its index:\n\n{string.Join("\n", removedRoots)}\n\nMusic files will not be deleted, and saved playlist entries will remain.",
                PrimaryButtonText = "Remove from library", CloseButtonText = "Keep folders",
                DefaultButton = ContentDialogButton.Close, XamlRoot = ShellRoot.XamlRoot
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        }
        try
        {
            if (removedRoots.Length > 0)
                await Task.Run(() => _store.RemoveTracksUnderUnselectedRoots(removedRoots, remainingRoots));
            if (removedRoots.Length > 0)
                await Task.Run(() => _store.PruneUnreferencedArtwork(Path.Combine(_appData, "Artwork")));
            _store.SetSetting("library-roots", JsonSerializer.Serialize(remainingRoots));
            _store.SetSetting("library-roots-configured", "true");
            ConfigureGroupView(); RefreshPlaylists(); RefreshLibrary();
            if (addedRoots.Length > 0) StartScan(addedRoots);
            else await ShowNoticeAsync("Library folders saved.");
        }
        catch (Exception ex)
        {
            LocalAppLog.Shared.Error("library-roots", "Could not apply library folder changes.", ex);
            await ShowNoticeAsync($"Could not apply the library folder changes: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private static string? NormalizeRoot(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { LocalAppLog.Shared.Warning("library-roots", $"Ignored invalid saved library folder '{path}'.", ex); return null; }
    }

    private void Scan_Click(object sender, RoutedEventArgs e)
    {
        var roots = ReadJsonSetting("library-roots", Array.Empty<string>());
        if (roots.Length == 0) { _ = ShowNoticeAsync("Add a music folder in Library folders before starting a scan."); return; }
        StartScan(roots);
    }

    private void ScanPause_Click(object sender, RoutedEventArgs e)
    {
        if (_scanControl is null) return;
        if (ScanPauseButton.Content?.ToString() == "Pause scan")
        { _scanControl.Pause(); _scanPaused = true; ScanPauseButton.Content = "Resume scan"; UpdateScanStatusText(); }
        else { _scanControl.Resume(); _scanPaused = false; ScanPauseButton.Content = "Pause scan"; UpdateScanStatusText(); }
    }

    private void ScanCancel_Click(object sender, RoutedEventArgs e) => _scanControl?.Cancel();

    private void UpdateScanStatusText()
    {
        if (_scanControl is null || _windowClosed) return;
        var path = _scanPaused ? "Paused" : string.IsNullOrWhiteSpace(_scanCurrentPath) ? "Finishing scan…" : _scanCurrentPath;
        ScanStatusText.Text = $"{(_scanPaused ? "Paused · " : "")}{FormatElapsed(_scanStartedUtc)} · {_scanFilesFound:N0} tracks · {_scanDirectoriesVisited:N0} folders · {path}";
        ToolTipService.SetToolTip(ScanStatusText, path);
    }

    private static string FormatElapsed(DateTimeOffset? startedUtc)
    {
        if (startedUtc is null) return "0:00";
        var elapsed = DateTimeOffset.UtcNow - startedUtc.Value;
        return elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"m\:ss");
    }

    private sealed record LibraryQueryRequest(string? Search, string View, PlaylistSummary? SelectedPlaylist,
        TrackSort Sort, bool Descending, string? GroupColumn, string? GroupValue, int Offset);
    private sealed record LibraryPageResult(IReadOnlyList<Track> Tracks, IReadOnlyList<PlaylistTrackRow> PlaylistRows, int TotalCount, int PageIndex);

    private LibraryQueryRequest CaptureLibraryQuery(string? search) => new(search, _view, _selectedPlaylist,
        _sort, _descending, GroupColumn(), GroupList.SelectedItem?.ToString(), _pageIndex * LibraryPageSize);

    private LibraryPageResult ReadLibraryPage(LibraryQueryRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.View == "Playlists" && request.SelectedPlaylist is { } selected)
        {
            var entries = _store.GetPlaylistEntriesPage(selected.Id, request.Search, request.Offset, LibraryPageSize);
            cancellationToken.ThrowIfCancellationRequested();
            var total = _store.CountPlaylistEntries(selected.Id, request.Search);
            return new(entries.Where(entry => entry.Track is not null).Select(entry => entry.Track!).ToArray(),
                entries.Select(entry => new PlaylistTrackRow(entry)).ToArray(), total, request.Offset / LibraryPageSize);
        }

        var filter = request.View switch { "Favorites" => "favorites", "Most Played" => "most-played", "Recently Played" => "recent", _ => null };
        var totalCount = _store.CountTracks(request.Search, request.Sort, request.Descending, filter, request.GroupColumn, request.GroupValue);
        cancellationToken.ThrowIfCancellationRequested();
        var tracks = _store.GetTracksPage(request.Search, request.Sort, request.Descending, filter, request.GroupColumn,
            request.GroupValue, request.Offset, LibraryPageSize);
        return new(tracks, Array.Empty<PlaylistTrackRow>(), totalCount, request.Offset / LibraryPageSize);
    }

    private void RefreshLibrary()
    {
        CancelPendingLibrarySearch();
        var result = ReadLibraryPage(CaptureLibraryQuery(SearchBox?.Text), CancellationToken.None);
        ApplyLibraryPage(result);
    }

    private void CancelPendingLibrarySearch()
    {
        _librarySearchRevision++;
        var pending = _librarySearchCancellation;
        _librarySearchCancellation = null;
        pending?.Cancel();
    }

    private async Task RefreshLibraryFromSearchAsync()
    {
        if (_windowClosed) return;
        var request = CaptureLibraryQuery(SearchBox?.Text);
        var revision = ++_librarySearchRevision;
        var cancellation = new CancellationTokenSource();
        var previous = _librarySearchCancellation;
        _librarySearchCancellation = cancellation;
        previous?.Cancel();
        try
        {
            var result = await Task.Run(() => ReadLibraryPage(request, cancellation.Token), cancellation.Token);
            if (!_windowClosed && !cancellation.IsCancellationRequested && revision == _librarySearchRevision)
                ApplyLibraryPage(result);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LocalAppLog.Shared.Warning("library-search", "Search refresh failed.", ex);
            if (!_windowClosed && revision == _librarySearchRevision)
                _ = ShowNoticeAsync("Search could not be completed. See the local log for details.", InfoBarSeverity.Error);
        }
        finally
        {
            if (ReferenceEquals(_librarySearchCancellation, cancellation)) _librarySearchCancellation = null;
            cancellation.Dispose();
        }
    }

    private void ApplyLibraryPage(LibraryPageResult result)
    {
        _pageIndex = result.PageIndex;
        _totalTrackCount = result.TotalCount;
        _tracks.Clear(); foreach (var track in result.Tracks) _tracks.Add(track);
        _playlistRows.Clear(); foreach (var entry in result.PlaylistRows) _playlistRows.Add(entry);
        var firstTrack = _totalTrackCount == 0 ? 0 : _pageIndex * LibraryPageSize + 1;
        var lastTrack = Math.Min((_pageIndex + 1) * LibraryPageSize, _totalTrackCount);
        TrackCountText.Text = _view == "Playlists"
            ? _selectedPlaylist is null ? $"{_playlists.Count:N0} playlists" : $"{_totalTrackCount:N0} entries"
            : _totalTrackCount == 0 ? "No matching tracks" : $"{firstTrack:N0}–{lastTrack:N0} of {_totalTrackCount:N0} tracks";
        PageStatusText.Text = _totalTrackCount == 0 ? "No tracks" : $"{firstTrack:N0}–{lastTrack:N0} of {_totalTrackCount:N0}";
        PlaylistPageStatusText.Text = _totalTrackCount == 0 ? "No entries" : $"{firstTrack:N0}–{lastTrack:N0} of {_totalTrackCount:N0}";
        PreviousPageButton.IsEnabled = _pageIndex > 0;
        NextPageButton.IsEnabled = (_pageIndex + 1) * LibraryPageSize < _totalTrackCount;
        PlaylistPager.Visibility = _view == "Playlists" && _selectedPlaylist is not null && _totalTrackCount > LibraryPageSize
            ? Visibility.Visible : Visibility.Collapsed;
        LibraryPager.Visibility = _view != "Playlists" && _view != "Now Playing" && _totalTrackCount > LibraryPageSize
            ? Visibility.Visible : Visibility.Collapsed;
        SortBox.Visibility = SortDirectionButton.Visibility = _view == "Playlists" ? Visibility.Collapsed : Visibility.Visible;
        var playlistActive = _view == "Playlists";
        var activeView = _view == "Now Playing" ? NowPlayingView : playlistActive ? PlaylistView : LibraryView;
        var enteringView = activeView.Visibility != Visibility.Visible;
        EmptyState.Visibility = _tracks.Count == 0 && !playlistActive ? Visibility.Visible : Visibility.Collapsed;
        PlaylistView.Visibility = playlistActive ? Visibility.Visible : Visibility.Collapsed;
        LibraryView.Visibility = _view != "Now Playing" && !playlistActive ? Visibility.Visible : Visibility.Collapsed;
        NowPlayingView.Visibility = _view == "Now Playing" ? Visibility.Visible : Visibility.Collapsed;
        if (enteringView) AnimateContentEntrance(activeView);
        if (_view == "Playlists") ViewTitle.Text = "Playlists";
        else ViewTitle.Text = GroupList.SelectedItem is string group ? $"{_view} · {group}" : _view;

        var hasSelectedPlaylist = playlistActive && _selectedPlaylist is not null;
        PlaylistDetailTitle.Text = _selectedPlaylist?.Name ?? "Choose a playlist";
        PlaylistDetailCount.Text = _selectedPlaylist is null
            ? $"{_playlists.Count:N0} {(_playlists.Count == 1 ? "playlist" : "playlists")} stored on this PC"
            : $"{_totalTrackCount:N0} entries · page {_pageIndex + 1:N0}";
        PlaylistTrackList.Visibility = hasSelectedPlaylist && _playlistRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PlaylistEmptyState.Visibility = !hasSelectedPlaylist || _playlistRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PlaylistEmptyTitle.Text = _selectedPlaylist is null ? (_playlists.Count == 0 ? "No playlists yet" : "Select a playlist") : "This playlist is empty";
        PlaylistEmptyMessage.Text = _selectedPlaylist is null
            ? (_playlists.Count == 0 ? "Create a playlist or import an M3U8 file to get started." : "Choose a playlist from the list to view and play its tracks.")
            : "Playlist entries are kept locally. Tracks can be unavailable if their files were moved or removed.";
        RenamePlaylistButton.IsEnabled = ExportPlaylistButton.IsEnabled = DeletePlaylistButton.IsEnabled = hasSelectedPlaylist;
        PlayPlaylistButton.IsEnabled = hasSelectedPlaylist && _totalTrackCount > 0;
        PlayPauseButton.IsEnabled = _playback.CurrentTrack is not null || _tracks.Count > 0;
    }

    private static bool Contains(string value, string query) => value.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    private void AnimateContentEntrance(UIElement enteringView)
    {
        foreach (var view in new UIElement[] { LibraryView, PlaylistView, NowPlayingView })
        {
            view.Opacity = 1;
            view.Translation = Vector3.Zero;
            ElementCompositionPreview.SetIsTranslationEnabled(view, false);
        }

        var style = _store.GetSetting("motion-style") ?? "Subtle";
        if (style == "Off" || !_uiSettings.AnimationsEnabled) return;

        ElementCompositionPreview.SetIsTranslationEnabled(enteringView, true);
        var compositor = ElementCompositionPreview.GetElementVisual(enteringView).Compositor;
        var expressive = style == "Expressive";
        var duration = TimeSpan.FromMilliseconds(expressive ? 220 : 130);
        var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(.16f, 1f), new Vector2(.3f, 1f));
        enteringView.Opacity = 0;
        enteringView.Translation = new Vector3(0, expressive ? 16 : 8, 0);
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.Target = "Opacity";
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1, ease);
        fade.Duration = duration;
        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.Target = "Translation";
        slide.InsertKeyFrame(0, enteringView.Translation);
        slide.InsertKeyFrame(1, Vector3.Zero, ease);
        slide.Duration = duration;
        enteringView.StartAnimation(fade);
        enteringView.StartAnimation(slide);
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var item = args.SelectedItem as NavigationViewItem;
        if (item?.Tag?.ToString() == "Settings") { _ = ShowSettingsAsync(); return; }
        var next = item?.Tag?.ToString() ?? "Songs";
        if (next == "Playlists") { _selectedPlaylist = null; RefreshPlaylists(); }
        else _selectedPlaylist = null;
        _view = next;
        _pageIndex = 0;
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

    private void GroupList_SelectionChanged(object sender, SelectionChangedEventArgs e) { _pageIndex = 0; RefreshLibrary(); }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _pageIndex = 0;
        CancelPendingLibrarySearch();
        if (_searchDebounce is null) RefreshLibrary();
        else { _searchDebounce.Stop(); _searchDebounce.Start(); }
    }

    private void PreviousPage_Click(object sender, RoutedEventArgs e)
    {
        if (_pageIndex <= 0) return;
        _pageIndex--; RefreshLibrary();
    }

    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
        if ((_pageIndex + 1) * LibraryPageSize >= _totalTrackCount) return;
        _pageIndex++; RefreshLibrary();
    }

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_uiReady && SortBox.SelectedIndex >= 0 && SortBox.SelectedIndex < 11)
        {
            _sort = SortBox.SelectedIndex switch { 0 => TrackSort.Title, 1 => TrackSort.Artist, 2 => TrackSort.Album, 3 => TrackSort.Genre,
                4 => TrackSort.Year, 5 => TrackSort.Added, 6 => TrackSort.Duration, 7 => TrackSort.PlayCount,
                8 => TrackSort.LastPlayed, 9 => TrackSort.Path, _ => TrackSort.Rating };
            _pageIndex = 0;
            RefreshLibrary();
        }
    }

    private void SortDirection_Click(object sender, RoutedEventArgs e)
    {
        _descending = !_descending; _pageIndex = 0; SortDirectionButton.Content = _descending ? "Descending" : "Ascending"; RefreshLibrary();
    }

    private void TrackList_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (TrackList.SelectedItem is Track track) PlayTrack(track, true, _pageIndex * LibraryPageSize + TrackList.SelectedIndex);
    }

    private void TrackList_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter || TrackList.SelectedItem is not Track track) return;
        PlayTrack(track, true, _pageIndex * LibraryPageSize + TrackList.SelectedIndex);
        e.Handled = true;
    }

    private void PlaylistTrackList_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (PlaylistTrackList.SelectedItem is PlaylistTrackRow { Entry.Track: { } track } row && _selectedPlaylist is not null)
        {
            var index = _pageIndex * LibraryPageSize + PlaylistTrackList.SelectedIndex;
            PlayTrack(track, true, index);
        }
    }

    private void PlaylistTrackList_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter || PlaylistTrackList.SelectedItem is not PlaylistTrackRow { Entry.Track: { } track }) return;
        PlayTrack(track, true, _pageIndex * LibraryPageSize + PlaylistTrackList.SelectedIndex);
        e.Handled = true;
    }

    private void TrackList_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void PlayTrack(Track track, bool resetQueue, int? queueIndex = null)
    {
        if (resetQueue)
        {
            _queue.Clear(); _queue.AddRange(GetCurrentQueuePaths());
            _queueIndex = queueIndex is { } selectedIndex && selectedIndex >= 0 && selectedIndex < _queue.Count && SameTrack(_queue[selectedIndex], track.Path)
                ? selectedIndex
                : _queue.FindIndex(item => SameTrack(item, track.Path));
            if (_shuffle) QueueNavigation.ShuffleUpcoming(_queue, Math.Clamp(_queueIndex + 1, 0, _queue.Count));
        }
        else if (queueIndex is { } requestedIndex && requestedIndex >= 0 && requestedIndex < _queue.Count) _queueIndex = requestedIndex;
        else
        {
            var matchingIndex = _queue.FindIndex(item => SameTrack(item, track.Path));
            if (matchingIndex >= 0) _queueIndex = matchingIndex;
        }
        _playback.Play(track); _countedCurrentPlay = false; _heardMilliseconds = 0; _lastPlayCountPosition = 0; _crossfadeInProgress = false; _crossfadeFailureSource = null; _crossfadeSourceQueueIndex = null; _repeatA = _repeatB = null;
        UpdatePlaybackModeControls();
        UpdateCurrentTrack(track); UpdateSystemMediaControls(track, true); SetPlayPauseVisual(true); SeekSlider.IsEnabled = true; SaveSession();
    }

    private IReadOnlyList<string> GetCurrentQueuePaths()
    {
        if (_view == "Playlists" && _selectedPlaylist is not null) return _store.GetPlaylistPaths(_selectedPlaylist.Id, SearchBox.Text);
        var filter = _view switch { "Favorites" => "favorites", "Most Played" => "most-played", "Recently Played" => "recent", _ => null };
        return _store.GetTrackPaths(SearchBox.Text, _sort, _descending, filter, GroupColumn(), GroupList.SelectedItem?.ToString());
    }

    private Track? ResolveQueueTrack(int index) => index >= 0 && index < _queue.Count ? _store.GetTrack(_queue[index]) : null;

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_playback.CurrentTrack is null)
        {
            if (_tracks.Count > 0) PlayTrack(_tracks[0], true);
            else SetPlayPauseVisual(false);
            return;
        }
        if (_playback.IsPlaying) { CancelCrossfadeAndRestoreQueue(); _playback.Pause(); if (_systemControls is not null) _systemControls.PlaybackStatus = MediaPlaybackStatus.Paused; SetPlayPauseVisual(false); }
        else { _lastPlayCountPosition = _playback.Position; _playback.PlayLoaded(); UpdateSystemMediaControls(_playback.CurrentTrack, true); SetPlayPauseVisual(true); }
    }

    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        CancelCrossfadeAndRestoreQueue();
        if (_playback.Position > 3000) { _playback.Seek(0); return; }
        for (var previousIndex = _queueIndex - 1; previousIndex >= 0; previousIndex--)
        {
            if (ResolveQueueTrack(previousIndex) is not { } previous) continue;
            _queueIndex = previousIndex;
            PlayTrack(previous, false, _queueIndex);
            return;
        }
    }

    private void Next_Click(object sender, RoutedEventArgs e) => AdvanceQueue(false);

    private void AdvanceQueue(bool automatic)
    {
        var wasEmpty = _queue.Count == 0;
        EnsureQueueInitialized();
        if (wasEmpty && _shuffle) QueueNavigation.ShuffleUpcoming(_queue, Math.Clamp(_queueIndex + 1, 0, _queue.Count));
        if (_queue.Count == 0) return;
        for (var attempt = 0; attempt <= _queue.Count; attempt++)
        {
            var next = QueueNavigation.NextIndex(_queue.Count, _queueIndex, automatic, _repeatMode);
            if (next < 0) break;
            if (ResolveQueueTrack(next) is not { } track)
            {
                if (next == _queueIndex) { _playback.Stop(); break; }
                _queueIndex = next;
                continue;
            }
            if (_playback.IsPlaying && !SameTrack(_crossfadeFailureSource, _playback.CurrentTrack?.Path) && _crossfadeSeconds > 0)
                StartCrossfade(next, _crossfadeSeconds * 1000);
            else PlayTrack(track, false, next);
            return;
        }
        CancelCrossfadeAndRestoreQueue();
        _playback.Stop(); if (_systemControls is not null) _systemControls.PlaybackStatus = MediaPlaybackStatus.Stopped; SetPlayPauseVisual(false);
    }

    private void StartCrossfade(int targetIndex, int durationMilliseconds)
    {
        _crossfadeSourceQueueIndex = _crossfadeInProgress ? _crossfadeSourceQueueIndex ?? _queueIndex : _queueIndex;
        if (ResolveQueueTrack(targetIndex) is not { } target) { AdvanceQueue(false); return; }
        _queueIndex = targetIndex;
        _crossfadeInProgress = true;
        _ = _playback.CrossfadeToAsync(target, durationMilliseconds);
    }

    private void CancelCrossfadeAndRestoreQueue()
    {
        if (!_crossfadeInProgress) return;
        _playback.CancelCrossfade();
        _crossfadeInProgress = false;
        _queueIndex = _crossfadeSourceQueueIndex is { } sourceIndex && sourceIndex >= 0 && sourceIndex < _queue.Count
            ? sourceIndex
            : _queue.FindIndex(item => SameTrack(item, _playback.CurrentTrack?.Path));
        _crossfadeSourceQueueIndex = null;
    }

    private void Shuffle_Click(object sender, RoutedEventArgs e)
    {
        _shuffle = !_shuffle; EnsureQueueInitialized();
        if (_shuffle) QueueNavigation.ShuffleUpcoming(_queue, Math.Clamp(_queueIndex + 1, 0, _queue.Count));
        UpdatePlaybackModeControls(); SaveSession(); _ = ShowNoticeAsync(_shuffle ? "Shuffle is on." : "Shuffle is off.");
    }

    private void EnsureQueueInitialized()
    {
        if (_queue.Count > 0) return;
        _queue.AddRange(GetCurrentQueuePaths()); _queueIndex = -1;
        if (_playback.CurrentTrack is not { } current) return;
        _queueIndex = _queue.FindIndex(item => SameTrack(item, current.Path));
        if (_queueIndex < 0) { _queue.Insert(0, current.Path); _queueIndex = 0; }
    }

    private void Repeat_Click(object sender, RoutedEventArgs e)
    {
        _repeatMode = _repeatMode switch { "Off" => "Queue", "Queue" => "Track", _ => "Off" };
        RepeatButton.Content = $"Repeat: {_repeatMode}"; SaveSession();
        AutomationProperties.SetItemStatus(RepeatButton, _repeatMode);
    }

    private void AbRepeat_Click(object sender, RoutedEventArgs e)
    {
        CancelCrossfadeAndRestoreQueue();
        var position = TimeSpan.FromMilliseconds(Math.Max(0, _playback.Position));
        if (_playback.CurrentTrack is null) return;
        if (_repeatA is null) { _repeatA = position; _repeatB = null; _ = ShowNoticeAsync("A–B repeat: mark B at the end of the passage."); }
        else if (_repeatB is null && position > _repeatA) { _repeatB = position; _ = ShowNoticeAsync("A–B repeat is set. Press again to clear."); }
        else { _repeatA = _repeatB = null; _ = ShowNoticeAsync("A–B repeat cleared."); }
        UpdatePlaybackModeControls();
        SaveSession();
    }

    private void UpdatePlaybackModeControls()
    {
        ShuffleButton.IsChecked = _shuffle;
        AutomationProperties.SetItemStatus(ShuffleButton, _shuffle ? "On" : "Off");
        ShuffleButton.Content = _shuffle ? "Shuffle: On" : "Shuffle";
        AbRepeatButton.IsChecked = _repeatB is not null ? true : _repeatA is not null ? null : false;
        AbRepeatButton.Content = _repeatB is not null ? "A–B: On" : _repeatA is not null ? "A–B: Set A" : "A–B";
        AutomationProperties.SetItemStatus(AbRepeatButton, _repeatB is not null ? "On" : _repeatA is not null ? "Mark point B" : "Off");
        AbRepeatButton.IsEnabled = _playback.CurrentTrack is not null;
    }

    private void SetPlayPauseVisual(bool playing)
    {
        PlayPauseButton.Content = playing ? "Pause" : "Play";
        PlayPauseButton.IsChecked = playing;
        AutomationProperties.SetName(PlayPauseButton, playing ? "Pause playback" : "Resume playback");
        AutomationProperties.SetItemStatus(PlayPauseButton, playing ? "Playing" : "Paused");
    }

    private void ShellRoot_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateResponsiveLayout(e.NewSize.Width);

    private void UpdateResponsiveLayout(double width)
    {
        if (PlayerBarGrid is null) return;
        var compact = width < 980;
        var narrow = width < 760;
        NavView.PaneDisplayMode = width < 900
            ? NavigationViewPaneDisplayMode.LeftCompact
            : NavigationViewPaneDisplayMode.Left;
        LibraryView.ColumnDefinitions[0].Width = new GridLength(compact ? 160 : ReadPanelWidth("browse-pane-width", 240, 160, 400));
        PlaylistView.ColumnDefinitions[0].Width = new GridLength(compact ? 160 : ReadPanelWidth("browse-pane-width", 240, 160, 400));
        PlayerBarGrid.ColumnDefinitions[0].Width = new GridLength(compact ? 150 : 200);
        PlayerBarGrid.ColumnDefinitions[2].Width = new GridLength(compact ? 240 : 240);
        PlayerBarGrid.RowDefinitions.Clear();
        if (compact)
        {
            PlayerBarGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            PlayerBarGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(PlayerTrackInfo, 0); Grid.SetColumn(PlayerTrackInfo, 0); Grid.SetColumnSpan(PlayerTrackInfo, 1);
            Grid.SetRow(TransportControlsGrid, 1); Grid.SetColumn(TransportControlsGrid, 0); Grid.SetColumnSpan(TransportControlsGrid, 3);
            Grid.SetRow(PlayerUtilities, 0); Grid.SetColumn(PlayerUtilities, 1); Grid.SetColumnSpan(PlayerUtilities, 2);
            PlayerArtist.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
            VolumeSlider.Width = narrow ? 64 : 84;
            QueueButton.Visibility = EqualizerButton.Visibility = Visibility.Visible;
            QueueButtonLabel.Visibility = EqualizerButtonLabel.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
            VolumeLabel.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
            PlayerBarGrid.ColumnSpacing = 8;
            ShellRoot.RowDefinitions[1].Height = new GridLength(112);

            NowPlayingView.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            NowPlayingView.ColumnDefinitions[1].Width = new GridLength(0);
            NowPlayingView.RowDefinitions.Clear();
            NowPlayingView.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            NowPlayingView.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(NowPlayingArtworkPanel, 0); Grid.SetColumn(NowPlayingArtworkPanel, 0);
            Grid.SetRow(NowPlayingLyricsPanel, 1); Grid.SetColumn(NowPlayingLyricsPanel, 0);
            NowPlayingLyricsPanel.Margin = new Thickness(8, 12, 8, 0);
            NowPlayingArtwork.Width = NowPlayingArtwork.Height = Math.Min(216, Math.Max(160, width * .28));
        }
        else
        {
            Grid.SetRow(PlayerTrackInfo, 0); Grid.SetColumn(PlayerTrackInfo, 0); Grid.SetColumnSpan(PlayerTrackInfo, 1);
            Grid.SetRow(TransportControlsGrid, 0); Grid.SetColumn(TransportControlsGrid, 1); Grid.SetColumnSpan(TransportControlsGrid, 1);
            Grid.SetRow(PlayerUtilities, 0); Grid.SetColumn(PlayerUtilities, 2); Grid.SetColumnSpan(PlayerUtilities, 1);
            PlayerArtist.Visibility = Visibility.Visible;
            QueueButton.Visibility = EqualizerButton.Visibility = Visibility.Visible;
            QueueButtonLabel.Visibility = EqualizerButtonLabel.Visibility = VolumeLabel.Visibility = Visibility.Visible;
            VolumeSlider.Width = 100;
            PlayerBarGrid.ColumnSpacing = 12;
            ShellRoot.RowDefinitions[1].Height = new GridLength(112);

            NowPlayingView.RowDefinitions.Clear();
            NowPlayingView.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            NowPlayingView.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            Grid.SetRow(NowPlayingArtworkPanel, 0); Grid.SetColumn(NowPlayingArtworkPanel, 0);
            Grid.SetRow(NowPlayingLyricsPanel, 0); Grid.SetColumn(NowPlayingLyricsPanel, 1);
            NowPlayingLyricsPanel.Margin = new Thickness(0, 36, 0, 0);
            NowPlayingArtwork.Width = NowPlayingArtwork.Height = 320;
        }
    }

    private async void PlayNext_Click(object sender, RoutedEventArgs e)
    {
        if (TrackFromSender(sender) is not { } track) return;
        EnsureQueueInitialized();
        var insertAt = Math.Clamp(_queueIndex + 1, 0, _queue.Count);
        _queue.Insert(insertAt, track.Path); if (_queueIndex >= 0 && insertAt <= _queueIndex) _queueIndex++;
        if (_shuffle) QueueNavigation.ShuffleUpcoming(_queue, insertAt + 1);
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
        var volume = Math.Clamp((int)Math.Round(e.NewValue), 0, 100);
        _playback.Volume = volume;
        if (_suppressVolumePersistence || _windowClosed) return;
        if (volume == _persistedVolume)
        {
            _pendingVolumeSetting = null;
            _volumeSaveDebounce?.Stop();
            return;
        }
        _pendingVolumeSetting = volume;
        _volumeSaveDebounce?.Stop();
        _volumeSaveDebounce?.Start();
    }

    private void PersistPendingVolumeSetting()
    {
        if (_pendingVolumeSetting is not int volume || volume == _persistedVolume) return;
        try
        {
            _store.SetSetting("volume", volume.ToString(CultureInfo.InvariantCulture));
            _persistedVolume = volume;
            _pendingVolumeSetting = null;
        }
        catch (Exception ex)
        {
            LocalAppLog.Shared.Error("settings", "Could not save the volume setting.", ex);
            _pendingVolumeSetting = null;
        }
    }

    private int ReadCrossfadeSeconds()
    {
        if (!int.TryParse(_store.GetSetting("crossfade-seconds"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)) return 0;
        return seconds is 2 or 3 or 5 or 8 or 10 ? seconds : 0;
    }

    private void SeekSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_updatingPosition && SeekSlider.IsEnabled)
        {
            CancelCrossfadeAndRestoreQueue();
            _playback.Seek((long)e.NewValue);
        }
    }

    private void Clock_Tick(object? sender, object e)
    {
        var position = Math.Max(0, _playback.Position); var duration = Math.Max(0, _playback.Duration);
        if (_scanControl is not null && DateTimeOffset.UtcNow - _lastScanStatusUpdateUtc >= TimeSpan.FromSeconds(1))
        { _lastScanStatusUpdateUtc = DateTimeOffset.UtcNow; UpdateScanStatusText(); }
        if (_playback.IsPlaying && _repeatA is not null && _repeatB is not null && position >= _repeatB.Value.TotalMilliseconds)
        {
            _playback.Seek((long)_repeatA.Value.TotalMilliseconds);
            position = Math.Max(0, _playback.Position);
        }
        _updatingPosition = true; SeekSlider.Maximum = Math.Max(1, duration); SeekSlider.Value = Math.Min(position, SeekSlider.Maximum); _updatingPosition = false;
        ElapsedText.Text = FormatTime(position); DurationText.Text = FormatTime(duration);
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
        var automaticNext = QueueNavigation.NextIndex(_queue.Count, _queueIndex, true, _repeatMode);
        if (_playback.IsPlaying && _repeatA is null && !_crossfadeInProgress && !SameTrack(_crossfadeFailureSource, _playback.CurrentTrack?.Path) && automaticNext >= 0 &&
            _crossfadeSeconds > 0 && duration > 0 && duration - position <= _crossfadeSeconds * 1000)
            StartCrossfade(automaticNext, _crossfadeSeconds * 1000);
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
        _crossfadeInProgress = false; _crossfadeFailureSource = null; _crossfadeSourceQueueIndex = null; _countedCurrentPlay = false; _heardMilliseconds = 0; _lastPlayCountPosition = 0;
        UpdateCurrentTrack(track); UpdateSystemMediaControls(track, true); SetPlayPauseVisual(true);
    });

    private static bool SameTrack(string? left, string? right) => left is not null && right is not null && left.Equals(right, StringComparison.OrdinalIgnoreCase);

    private void Playback_CrossfadeFailed(Track track) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!_crossfadeInProgress || _queueIndex < 0 || _queueIndex >= _queue.Count || !SameTrack(_queue[_queueIndex], track.Path)) return;
        _crossfadeInProgress = false;
        _crossfadeFailureSource = _playback.CurrentTrack?.Path;
        _queueIndex = _crossfadeSourceQueueIndex is { } sourceIndex && sourceIndex >= 0 && sourceIndex < _queue.Count
            ? sourceIndex
            : _queue.FindIndex(item => SameTrack(item, _playback.CurrentTrack?.Path));
        _crossfadeSourceQueueIndex = null;
        _ = ShowNoticeAsync($"Could not start {track.Title} during crossfade. Playback will continue; see the local log for details.", InfoBarSeverity.Error);
    });

    private void Playback_Failed(Track track) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!SameTrack(_playback.CurrentTrack?.Path, track.Path)) return;
        _crossfadeInProgress = false;
        SetPlayPauseVisual(false);
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
        SetArtwork(NowPlayingArtwork, track.ArtworkPath, 512);
        if (_store.GetSetting("accent-mode") == "Artwork" && _store.GetSetting("accent-manual") != "true") _ = ApplyArtworkAccentAsync(track.ArtworkPath);
    }

    private void Artwork_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Image image) SetArtwork(image, image.Tag?.ToString());
    }

    private void SetArtwork(Image image, string? path, int? preferredDecodeSize = null)
    {
        var scale = image.XamlRoot?.RasterizationScale ?? 1;
        var displaySize = Math.Max(image.ActualWidth, image.ActualHeight);
        if (!double.IsFinite(displaySize) || displaySize <= 0) displaySize = Math.Max(image.Width, image.Height);
        if (!double.IsFinite(displaySize) || displaySize <= 0) displaySize = 96;
        var decodeSize = preferredDecodeSize ?? (int)Math.Ceiling(displaySize * scale);
        image.Source = _artworkCache.Get(path, decodeSize);
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
        if (session.RepeatAMilliseconds is long repeatA && repeatA >= 0 && session.RepeatBMilliseconds is long repeatB && repeatB > repeatA)
        { _repeatA = TimeSpan.FromMilliseconds(repeatA); _repeatB = TimeSpan.FromMilliseconds(repeatB); }
        RepeatButton.Content = $"Repeat: {_repeatMode}";
        AutomationProperties.SetItemStatus(RepeatButton, _repeatMode);
        _queue.AddRange(session.Queue);
        if (session.TrackPath is { } path && _store.GetTrack(path) is { } current)
        {
            if (_queue.Count == 0) _queue.Add(current.Path);
            var savedIndex = session.QueueIndex;
            var occurrence = savedIndex >= 0 && savedIndex < session.Queue.Count && SameTrack(session.Queue[savedIndex], path)
                ? session.Queue.Take(savedIndex + 1).Count(queuedPath => SameTrack(queuedPath, path)) - 1
                : 0;
            _queueIndex = Playlists.FindPathOccurrence(_queue, path, Math.Max(0, occurrence));
            if (_queueIndex < 0) _queueIndex = _queue.FindIndex(queuedPath => SameTrack(queuedPath, path));
            if (_queueIndex < 0)
            {
                _queue.Insert(0, current.Path);
                _queueIndex = 0;
            }
            _playback.LoadPaused(current, session.PositionMilliseconds); _countedCurrentPlay = false; _heardMilliseconds = 0; _lastPlayCountPosition = session.PositionMilliseconds;
            UpdateCurrentTrack(current); UpdateSystemMediaControls(current, false); SeekSlider.IsEnabled = true; SetPlayPauseVisual(false);
        }
    }

    private void SaveSession()
    {
        try
        {
            var savedQueueIndex = _crossfadeInProgress ? _crossfadeSourceQueueIndex ?? _queueIndex : _queueIndex;
            _store.SaveSession(new(_playback.CurrentTrack?.Path, Math.Max(0, _playback.Position), _queue.ToArray(), _shuffle, _repeatMode,
                _repeatA is { } repeatA ? (long)repeatA.TotalMilliseconds : null,
                _repeatB is { } repeatB ? (long)repeatB.TotalMilliseconds : null,
                savedQueueIndex));
            _lastSessionSave = DateTime.UtcNow;
        }
        catch (Exception ex) { LocalAppLog.Shared.Error("session", "Could not save playback state.", ex); }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _windowClosed = true;
        CancelPendingLibrarySearch();
        LocalAppLog.Shared.Info("app", "Window closed.");
        _volumeSaveDebounce?.Stop();
        PersistPendingVolumeSetting();
        SaveSession(); _clock.Stop(); _searchDebounce?.Stop(); _playback.Dispose(); _scanControl?.Cancel(); _artworkCache.Clear();
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

    private async void ExportLogs_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker();
        picker.FileTypeChoices.Add("Bracken Vale diagnostic logs", [".zip"]);
        picker.SuggestedFileName = $"BrackenVale-logs-{DateTime.Now:yyyyMMdd-HHmmss}";
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        try
        {
            LocalAppLog.Shared.ExportTo(file.Path);
            await ShowNoticeAsync("Logs exported. Review the ZIP for local file paths before sharing.");
        }
        catch (Exception ex)
        {
            LocalAppLog.Shared.Error("logs", "Could not export diagnostic logs.", ex);
            await ShowNoticeAsync($"Could not export logs: {ex.Message}", InfoBarSeverity.Error);
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
        NavView.OpenPaneLength = ReadPanelWidth("navigation-pane-width", 224, 180, 360);
        var browseWidth = ReadPanelWidth("browse-pane-width", 240, 160, 400);
        LibraryView.ColumnDefinitions[0].Width = new GridLength(browseWidth);
        PlaylistView.ColumnDefinitions[0].Width = new GridLength(browseWidth);
        var items = NavView.MenuItems.OfType<NavigationViewItem>().ToList();
        var byTag = items.Where(item => item.Tag is not null).ToDictionary(item => item.Tag!.ToString()!, StringComparer.Ordinal);
        var order = ReadJsonSetting("navigation-order", Array.Empty<string>());
        var arranged = order.Where(byTag.ContainsKey).Select(key => byTag[key]).Concat(items.Where(item => !order.Contains(item.Tag?.ToString() ?? "", StringComparer.Ordinal))).ToArray();
        NavView.MenuItems.Clear();
        foreach (var item in arranged) NavView.MenuItems.Add(item);
        var hidden = ReadJsonSetting("hidden-panels", Array.Empty<string>()).ToHashSet(StringComparer.Ordinal);
        foreach (var item in arranged) item.Visibility = hidden.Contains(item.Tag?.ToString() ?? "") ? Visibility.Collapsed : Visibility.Visible;
    }

    private int ReadPanelWidth(string key, int fallback, int minimum, int maximum) =>
        int.TryParse(_store.GetSetting(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, minimum, maximum) : fallback;

    private void ApplyStoredAppearance()
    {
        ShellRoot.RequestedTheme = _store.GetSetting("theme") switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        SystemBackdrop = _store.GetSetting("window-material") switch
        {
            "Acrylic" => new DesktopAcrylicBackdrop(),
            "Opaque" => null,
            _ => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ? new MicaBackdrop() : null
        };
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
        var windowMaterial = new ComboBox { Header = "Window material", MinWidth = 300 };
        foreach (var value in new[] { "Mica", "Desktop Acrylic", "Opaque" }) windowMaterial.Items.Add(value);
        windowMaterial.SelectedItem = _store.GetSetting("window-material") switch { "Acrylic" => "Desktop Acrylic", "Opaque" => "Opaque", _ => "Mica" };
        var motionStyle = new ComboBox { Header = "Navigation motion", MinWidth = 260 };
        foreach (var value in new[] { "Off", "Subtle", "Expressive" }) motionStyle.Items.Add(value);
        motionStyle.SelectedItem = _store.GetSetting("motion-style") switch { "Off" => "Off", "Expressive" => "Expressive", _ => "Subtle" };
        var updateCheck = new ToggleSwitch { Header = "Check GitHub Releases weekly", IsOn = _store.GetSetting("check-updates") != "false" };
        var minimizeToTray = new ToggleSwitch { Header = "Minimize to the notification area", IsOn = _store.GetSetting("minimize-to-tray") == "true" };
        var manualAccent = new ToggleSwitch { Header = "Use a custom accent color", IsOn = _store.GetSetting("accent-manual") == "true" };
        var color = TryParseColor(_store.GetSetting("accent-color"), out var savedColor) ? savedColor : Color.FromArgb(255, 62, 125, 96);
        var colorPicker = new ColorPicker { Color = color, IsColorPreviewVisible = true, IsColorSliderVisible = true, IsColorChannelTextInputVisible = true, IsHexInputVisible = true };
        var crossfade = new ComboBox { Header = "Crossfade", MinWidth = 260 };
        foreach (var value in new[] { "Off", "2 seconds", "3 seconds", "5 seconds", "8 seconds", "10 seconds" }) crossfade.Items.Add(value);
        var oldCrossfade = _crossfadeSeconds;
        crossfade.SelectedIndex = oldCrossfade switch { 2 => 1, 3 => 2, 5 => 3, 8 => 4, 10 => 5, _ => 0 };
        var audioOutput = new ComboBox { Header = "Audio output", MinWidth = 300 };
        var audioOutputStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] };
        async Task RefreshAudioOutputsAsync()
        {
            var previousSelection = audioOutput.SelectedItem as ComboBoxItem;
            var selectedId = previousSelection?.Tag?.ToString() ?? _store.GetSetting("audio-output-device");
            var previousName = previousSelection?.Content?.ToString()?.Replace(" (not connected)", "", StringComparison.Ordinal)
                ?? _store.GetSetting("audio-output-name") ?? "Saved output";
            try
            {
                var devices = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector());
                audioOutput.Items.Clear();
                var defaultOutput = new ComboBoxItem { Content = "System default", Tag = "" };
                audioOutput.Items.Add(defaultOutput);
                foreach (var device in devices.Where(device => device.IsEnabled && !string.IsNullOrWhiteSpace(device.Id))
                             .GroupBy(device => device.Id, StringComparer.OrdinalIgnoreCase).Select(group => group.First())
                             .OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase))
                    audioOutput.Items.Add(new ComboBoxItem { Content = device.Name, Tag = device.Id });
                var selected = audioOutput.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == selectedId);
                if (selected is null && !string.IsNullOrWhiteSpace(selectedId))
                {
                    selected = new ComboBoxItem { Content = $"{previousName} (not connected)", Tag = selectedId };
                    audioOutput.Items.Add(selected);
                    audioOutputStatus.Text = "The saved device is unavailable. Windows will use its default output until that device reconnects.";
                }
                else audioOutputStatus.Text = "Choose a connected Windows output, including Bluetooth headphones or speakers. Refresh after connecting a device.";
                audioOutput.SelectedItem = selected ?? defaultOutput;
            }
            catch (Exception ex)
            {
                LocalAppLog.Shared.Warning("audio-devices", "Could not enumerate Windows audio output devices.", ex);
                audioOutput.Items.Clear();
                var defaultOutput = new ComboBoxItem { Content = "System default", Tag = "" };
                audioOutput.Items.Add(defaultOutput);
                if (!string.IsNullOrWhiteSpace(selectedId))
                {
                    var savedOutput = new ComboBoxItem { Content = $"{previousName} (device list unavailable)", Tag = selectedId };
                    audioOutput.Items.Add(savedOutput);
                    audioOutput.SelectedItem = savedOutput;
                }
                else audioOutput.SelectedItem = defaultOutput;
                audioOutputStatus.Text = "Could not list audio devices. The selected device is retained; see the local log for details.";
            }
        }
        var refreshAudioOutputs = new Button { Content = "Refresh devices", VerticalAlignment = VerticalAlignment.Bottom };
        refreshAudioOutputs.Click += async (_, _) => await RefreshAudioOutputsAsync();
        await RefreshAudioOutputsAsync();
        var ignored = new TextBox { Header = "Ignored folders (one full path per line)", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 84, Text = string.Join(Environment.NewLine, ReadJsonSetting("ignored-directories", Array.Empty<string>())) };
        content.Children.Add(theme); content.Children.Add(accentMode); content.Children.Add(windowMaterial);
        content.Children.Add(new TextBlock { Text = "Mica is the Windows 11 default. Desktop Acrylic is more transparent; Windows may fall back to an opaque surface when transparency effects are unavailable.", TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] });
        content.Children.Add(motionStyle);
        content.Children.Add(new TextBlock { Text = "Transitions affect library pages only, never individual track rows, and follow the Windows animation accessibility setting.", TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] });
        content.Children.Add(updateCheck); content.Children.Add(minimizeToTray);
        var updateNow = new Button { Content = "Check for updates now" };
        var updateStatus = new TextBlock { Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], TextWrapping = TextWrapping.Wrap };
        updateNow.Click += async (_, _) => await CheckForUpdatesAsync(true, updateStatus);
        content.Children.Add(updateNow); content.Children.Add(updateStatus);
        content.Children.Add(manualAccent);
        content.Children.Add(new TextBlock { Text = "Custom accent", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
        content.Children.Add(colorPicker); content.Children.Add(crossfade); content.Children.Add(audioOutput); content.Children.Add(refreshAudioOutputs); content.Children.Add(audioOutputStatus); content.Children.Add(ignored);
        content.Children.Add(new TextBlock { Text = $"Crash and error logs stay on this PC:\n{LocalAppLog.Shared.FolderPath}", TextWrapping = TextWrapping.Wrap });
        var openLogs = new Button { Content = "Open log folder", HorizontalAlignment = HorizontalAlignment.Left };
        openLogs.Click += OpenLogsFolder_Click; content.Children.Add(openLogs);
        var exportLogs = new Button { Content = "Export diagnostic logs…", HorizontalAlignment = HorizontalAlignment.Left };
        exportLogs.Click += ExportLogs_Click; content.Children.Add(exportLogs);
        content.Children.Add(new TextBlock { Text = "Log archives may contain local file paths. Review before sharing.", TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] });
        content.Children.Add(new TextBlock { Text = "Library navigation · reorder with arrows, hide optional panels", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], Margin = new Thickness(0, 12, 0, 0) });
        var navigationWidth = new Slider { Minimum = 180, Maximum = 360, StepFrequency = 10, Value = ReadPanelWidth("navigation-pane-width", 224, 180, 360) };
        var browseWidth = new Slider { Minimum = 160, Maximum = 400, StepFrequency = 10, Value = ReadPanelWidth("browse-pane-width", 240, 160, 400) };
        var navigationWidthLabel = new TextBlock { Text = $"Navigation pane · {(int)navigationWidth.Value} px" };
        var browseWidthLabel = new TextBlock { Text = $"Browse pane · {(int)browseWidth.Value} px" };
        navigationWidth.ValueChanged += (_, args) => navigationWidthLabel.Text = $"Navigation pane · {(int)args.NewValue} px";
        browseWidth.ValueChanged += (_, args) => browseWidthLabel.Text = $"Browse pane · {(int)args.NewValue} px";
        AutomationProperties.SetName(navigationWidth, "Navigation pane width");
        AutomationProperties.SetName(browseWidth, "Browse pane width");
        content.Children.Add(navigationWidthLabel); content.Children.Add(navigationWidth);
        content.Children.Add(browseWidthLabel); content.Children.Add(browseWidth);
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
        var chosenWindowMaterial = windowMaterial.SelectedItem?.ToString() switch { "Desktop Acrylic" => "Acrylic", "Opaque" => "Opaque", _ => "Mica" };
        var chosenMotionStyle = motionStyle.SelectedItem?.ToString() ?? "Subtle";
        var crossfadeSeconds = crossfade.SelectedIndex switch { 1 => 2, 2 => 3, 3 => 5, 4 => 8, 5 => 10, _ => 0 };
        var selectedAudioOutput = audioOutput.SelectedItem as ComboBoxItem;
        var audioOutputId = selectedAudioOutput?.Tag?.ToString() ?? "";
        var audioOutputName = selectedAudioOutput?.Content?.ToString()?.Replace(" (not connected)", "", StringComparison.Ordinal)
            .Replace(" (device list unavailable)", "", StringComparison.Ordinal) ?? "System default";
        var navigationPaneWidth = (int)Math.Round(navigationWidth.Value);
        var browsePaneWidth = (int)Math.Round(browseWidth.Value);
        string[] ignoredPaths;
        try
        {
            ignoredPaths = ignored.Text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(path => Path.GetFullPath(path)).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { RestoreNavigation(); await ShowNoticeAsync($"One ignored folder path is invalid: {ex.Message}", InfoBarSeverity.Warning); return; }
        _store.SetSetting("theme", chosenTheme); _store.SetSetting("accent-mode", chosenAccent);
        _store.SetSetting("window-material", chosenWindowMaterial); _store.SetSetting("motion-style", chosenMotionStyle);
        _store.SetSetting("accent-manual", manualAccent.IsOn ? "true" : "false"); _store.SetSetting("accent-color", $"#{colorPicker.Color.R:X2}{colorPicker.Color.G:X2}{colorPicker.Color.B:X2}");
        _store.SetSetting("check-updates", updateCheck.IsOn ? "true" : "false"); _store.SetSetting("crossfade-seconds", crossfadeSeconds.ToString());
        _crossfadeSeconds = crossfadeSeconds;
        _store.SetSetting("audio-output-device", audioOutputId); _store.SetSetting("audio-output-name", audioOutputName);
        _playback.SelectAudioOutputDevice(audioOutputId);
        _store.SetSetting("minimize-to-tray", minimizeToTray.IsOn ? "true" : "false"); ApplyTraySetting();
        _store.SetSetting("ignored-directories", JsonSerializer.Serialize(ignoredPaths));
        _store.SetSetting("navigation-pane-width", navigationPaneWidth.ToString(CultureInfo.InvariantCulture));
        _store.SetSetting("browse-pane-width", browsePaneWidth.ToString(CultureInfo.InvariantCulture));
        NavView.OpenPaneLength = navigationPaneWidth;
        LibraryView.ColumnDefinitions[0].Width = new GridLength(browsePaneWidth);
        PlaylistView.ColumnDefinitions[0].Width = new GridLength(browsePaneWidth);
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
            var assembly = typeof(MainWindow).Assembly;
            var currentVersionText = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString(3);
            var current = ReleaseVersion.TryParse(currentVersionText, out var parsedCurrent) ? parsedCurrent : new ReleaseVersion(0, 1, 0, null);
            var release = await GitHubUpdates.GetLatestAsync(current.IsPrerelease);
            if (release is null || !ReleaseVersion.TryParse(release.Tag, out var latest) || latest.CompareTo(current) <= 0)
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
        _playlists.Clear(); foreach (var playlist in _store.GetPlaylistSummaries()) _playlists.Add(playlist);
        if (selectedId is not null) PlaylistList.SelectedItem = _playlists.FirstOrDefault(item => item.Id == selectedId);
    }

    private void PlaylistList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedPlaylist = PlaylistList.SelectedItem as PlaylistSummary;
        _pageIndex = 0;
        RefreshLibrary();
    }

    private async void NewPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var name = new TextBox { PlaceholderText = "Playlist name", MinWidth = 280 };
        var dialog = new ContentDialog { Title = "Create playlist", Content = name, PrimaryButtonText = "Create", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = ShellRoot.XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(name.Text)) return;
        var created = _store.CreatePlaylist(name.Text);
        _selectedPlaylist = _store.GetPlaylistSummaries().FirstOrDefault(item => item.Id == created.Id);
        RefreshPlaylists(); RefreshLibrary();
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

    private async void RenamePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPlaylist is null) return;
        var name = new TextBox { Text = _selectedPlaylist.Name, MinWidth = 320, MaxLength = 120 };
        AutomationProperties.SetName(name, "Playlist name");
        var dialog = new ContentDialog { Title = "Rename playlist", Content = name, PrimaryButtonText = "Save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = ShellRoot.XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(name.Text)) return;
        try { _store.RenamePlaylist(_selectedPlaylist.Id, name.Text); RefreshPlaylists(); RefreshLibrary(); }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
        { await ShowNoticeAsync($"Could not rename playlist: {ex.Message}", InfoBarSeverity.Warning); }
    }

    private void PlayPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPlaylist is null) return;
        for (var offset = 0; offset < _totalTrackCount; offset += LibraryPageSize)
        {
            var entries = _store.GetPlaylistEntriesPage(_selectedPlaylist.Id, SearchBox.Text, offset, LibraryPageSize);
            var playable = entries.Select((entry, localIndex) => (entry, localIndex)).FirstOrDefault(item => item.entry.Track is not null);
            if (playable.entry?.Track is not { } track) continue;
            var index = offset + playable.localIndex;
            PlayTrack(track, true, index);
            return;
        }
        _ = ShowNoticeAsync("None of this playlist’s saved files are currently in the indexed library.", InfoBarSeverity.Warning);
    }

    private async void AddToPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (TrackFromSender(sender) is not { } track) return;
        RefreshPlaylists();
        if (_playlists.Count == 0) { await ShowNoticeAsync("Create a playlist first."); return; }
        var chooser = new ComboBox { ItemsSource = _playlists, DisplayMemberPath = "Name", SelectedIndex = 0, MinWidth = 280 };
        var dialog = new ContentDialog { Title = $"Add {track.Title} to playlist", Content = chooser, PrimaryButtonText = "Add", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = ShellRoot.XamlRoot };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && chooser.SelectedItem is PlaylistSummary playlist)
        {
            _store.AddToPlaylist(playlist.Id, [track.Path]); RefreshPlaylists(); await ShowNoticeAsync($"Added to {playlist.Name}.");
        }
    }

    private void RemoveFromPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPlaylist is null) return;
        if ((sender as FrameworkElement)?.Tag is not int position) return;
        _store.RemoveFromPlaylist(_selectedPlaylist.Id, position);
        _selectedPlaylist = _store.GetPlaylistSummaries().FirstOrDefault(item => item.Id == _selectedPlaylist.Id);
        var remainingFilteredEntries = _selectedPlaylist is null ? 0 : _store.CountPlaylistEntries(_selectedPlaylist.Id, SearchBox.Text);
        if (_pageIndex > 0 && _pageIndex * LibraryPageSize >= remainingFilteredEntries) _pageIndex--;
        RefreshLibrary(); RefreshPlaylists();
    }

    private async void EditTags_Click(object sender, RoutedEventArgs e)
    {
        if (TrackFromSender(sender) is not { } track) return;
        IReadOnlyDictionary<string, string> customValues;
        IReadOnlyDictionary<string, string> additionalValues;
        try
        {
            customValues = TagEditor.ReadCustomFields(track.Path);
            additionalValues = TagEditor.ReadAdditionalStandardFields(track.Path);
        }
        catch (Exception ex)
        {
            LocalAppLog.Shared.Error("tag-editor", $"Could not read tags for {track.Path}.", ex);
            await ShowNoticeAsync($"Could not read this file's tags: {ex.Message}");
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
        var additionalFields = new Dictionary<string, TextBox>(StringComparer.OrdinalIgnoreCase);
        var additionalTagFields = new StackPanel { Spacing = 8, Margin = new Thickness(2) };
        additionalTagFields.Children.Add(new TextBlock { Text = "Fields unsupported by this file format are ignored. Use custom tags below for format-specific text fields.", TextWrapping = TextWrapping.Wrap, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        foreach (var field in TagEditor.AdditionalStandardFields)
        {
            var editor = AddTextField(field.Label, additionalValues[field.Key]);
            additionalFields[field.Key] = editor;
            additionalTagFields.Children.Add(editor);
        }
        fields.Children.Add(new Expander { Header = "Additional standard tags", IsExpanded = false, Content = additionalTagFields });
        fields.Children.Add(artPicker); fields.Children.Add(artworkPreview); fields.Children.Add(custom);
        var validation = new TextBlock { TextWrapping = TextWrapping.Wrap };
        fields.Children.Add(validation);
        var scroll = new ScrollViewer { Content = fields, MaxHeight = 620, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var dialog = new ContentDialog { Title = "Preview and edit tags", Content = scroll, PrimaryButtonText = "Save tags", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = ShellRoot.XamlRoot };
        uint parsedYear = 0, parsedNumber = 0;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (!TryParseOptionalUInt(year.Text, out parsedYear) || parsedYear > 9999)
            { validation.Text = "Year must be a whole number from 0 to 9999, or blank to clear it."; args.Cancel = true; return; }
            if (!TryParseOptionalUInt(number.Text, out parsedNumber))
            { validation.Text = "Track number must be a whole number, or blank to clear it."; args.Cancel = true; return; }
            validation.Text = string.Empty;
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        TagBackup? backup = null;
        var tagsWritten = false;
        try
        {
            var customFields = ParseCustomFields(custom.Text);
            var edit = new TagEdit(title.Text, artist.Text, album.Text, albumArtist.Text, genre.Text,
                parsedYear, parsedNumber,
                ArtworkPath: string.IsNullOrWhiteSpace(artwork.Text) ? null : artwork.Text,
                CustomFields: customFormat is null ? null : customFields,
                AdditionalFields: additionalFields.ToDictionary(field => field.Key, field => field.Value.Text, StringComparer.OrdinalIgnoreCase));
            backup = await SaveTagsWithProgressAsync("Saving tag changes", (progress, token) =>
                new TagEditor(Path.Combine(_appData, "TagBackups")).SaveAsync(track.Path, edit, progress, token));
            if (backup is null) return;
            tagsWritten = true;
            _store.RecordTagBackup(backup);
            await RefreshEditedTrackAsync(track.Path);
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

    private async Task RefreshEditedTrackAsync(string path)
    {
        var track = await Task.Run(() =>
        {
            var scanned = TrackReader.Read(path, Path.Combine(_appData, "Artwork"));
            _store.UpsertTrack(scanned);
            return scanned;
        });
        if (SameTrack(_playback.CurrentTrack?.Path, track.Path))
        {
            _playback.UpdateTrackMetadata(track);
            UpdateCurrentTrack(track);
            UpdateSystemMediaControls(track, _playback.IsPlaying);
        }
        RefreshLibrary();
    }

    private static TextBox AddTextField(string name, string value) => new() { Header = name, Text = value, MinWidth = 330 };

    private static bool TryParseOptionalUInt(string value, out uint result)
    {
        result = 0;
        return string.IsNullOrWhiteSpace(value) || uint.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out result);
    }

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
        var backups = _store.GetTagBackups(track.Path).Where(item => File.Exists(item.BackupPath)).ToArray();
        if (backups.Length == 0) { await ShowNoticeAsync("No saved tag backups are available for this track."); return; }
        var backupOptions = backups.Select((backup, index) => new TagBackupOption(backup,
            $"{backup.CreatedUtc.ToLocalTime():g} · {Path.GetFileName(backup.BackupPath)}{(index == 0 ? " · Latest" : "")}")).ToArray();
        var chooser = new ListView { ItemsSource = backupOptions, DisplayMemberPath = nameof(TagBackupOption.Label), SelectedIndex = 0, MinHeight = 100, MaxHeight = 240 };
        AutomationProperties.SetName(chooser, "Saved tag versions, newest first");
        var body = new StackPanel { Spacing = 8, MinWidth = 420 };
        body.Children.Add(new TextBlock { Text = "Choose one of the five most recent saved versions. Bracken Vale first saves the current file as a new undo snapshot.", TextWrapping = TextWrapping.Wrap });
        body.Children.Add(chooser);
        var confirm = new ContentDialog { Title = $"Restore tags for {track.Title}?", Content = body, PrimaryButtonText = "Restore selected version", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close, XamlRoot = ShellRoot.XamlRoot };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary || chooser.SelectedItem is not TagBackupOption selected) return;
        var restored = false;
        try
        {
            var currentSnapshot = await SaveTagsWithProgressAsync("Restoring saved tags", (progress, token) =>
                new TagEditor(Path.Combine(_appData, "TagBackups")).RestoreAsync(selected.Backup, progress, token));
            if (currentSnapshot is null) return;
            _store.RecordTagBackup(currentSnapshot);
            restored = true;
            await RefreshEditedTrackAsync(track.Path);
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

    private async Task<TagBackup?> SaveTagsWithProgressAsync(string title,
        Func<IProgress<TagEditProgress>, CancellationToken, Task<TagBackup>> saveOperation)
    {
        using var cancellation = new CancellationTokenSource();
        var status = new TextBlock { Text = "Preparing…", TextWrapping = TextWrapping.Wrap };
        var progressBar = new ProgressBar { IsIndeterminate = true, Minimum = 0, Maximum = 1, Height = 4 };
        var content = new StackPanel { Spacing = 10, MinWidth = 360 };
        content.Children.Add(status); content.Children.Add(progressBar);
        var dialog = new ContentDialog { Title = title, Content = content, CloseButtonText = "Cancel", XamlRoot = ShellRoot.XamlRoot };
        dialog.CloseButtonClick += (_, _) => cancellation.Cancel();
        var progress = new Progress<TagEditProgress>(value =>
        {
            if (value.TotalBytes <= 0) { progressBar.IsIndeterminate = true; status.Text = value.Phase; return; }
            progressBar.IsIndeterminate = false;
            progressBar.Maximum = value.TotalBytes;
            progressBar.Value = Math.Min(value.BytesCopied, value.TotalBytes);
            status.Text = $"{value.Phase} · {value.BytesCopied:N0} of {value.TotalBytes:N0} bytes";
        });
        var dialogResult = dialog.ShowAsync();
        try
        {
            var backup = await saveOperation(progress, cancellation.Token);
            dialog.Hide();
            await dialogResult;
            return backup;
        }
        catch (OperationCanceledException)
        {
            dialog.Hide();
            await dialogResult;
            return null;
        }
        catch
        {
            dialog.Hide();
            await dialogResult;
            throw;
        }
    }

    private async void Details_Click(object sender, RoutedEventArgs e)
    {
        var track = TrackFromSender(sender); if (track is null) return;
        try
        {
            var details = TrackInformation.Read(track.Path);
            var backups = _store.GetTagBackups(track.Path);
            var customTags = TagEditor.ReadCustomFields(track.Path);
            var additionalTags = TagEditor.ReadAdditionalStandardFields(track.Path);
            var additionalText = string.Join(Environment.NewLine, TagEditor.AdditionalStandardFields
                .Where(field => !string.IsNullOrWhiteSpace(additionalTags[field.Key]))
                .Select(field => $"{field.Label}: {additionalTags[field.Key]}"));
            if (string.IsNullOrWhiteSpace(additionalText)) additionalText = "None";
            var customText = customTags.Count == 0 ? "None" : string.Join(Environment.NewLine, customTags.Select(field => $"{field.Key}={field.Value}"));
            var backupText = backups.Count == 0 ? "No tag backups" : string.Join(Environment.NewLine,
                backups.Select((backup, index) => $"{index + 1}. {backup.CreatedUtc.ToLocalTime():g} · {backup.BackupPath}"));
            var text = $"Title: {track.Title}\nArtist: {track.Artist}\nAlbum: {track.Album}\nAlbum artist: {track.AlbumArtist}\nGenre: {track.Genre}\nYear: {track.Year}\nTrack: {track.TrackNumber}\n\nAdditional standard tags\n{additionalText}\n\nCustom tags\n{customText}\n\nPath\n{details.Path}\n\nContainer\n{details.Container}\n\nDuration\n{details.Duration}\n\nBitrate\n{details.BitrateKbps} kbps\n\nSample rate\n{details.SampleRateHz:N0} Hz\n\nBit depth\n{details.BitsPerSample} bit\n\nFile size\n{details.FileSize:N0} bytes\n\nModified\n{details.ModifiedUtc:u}\n\nRecent tag backups\n{backupText}";
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
                backup = await SaveTagsWithProgressAsync("Saving embedded lyrics", (progress, token) =>
                    new TagEditor(Path.Combine(_appData, "TagBackups")).SaveAsync(track.Path, new TagEdit(Lyrics: lyricsText), progress, token));
                if (backup is null) return;
            }
            lyricsWritten = true;
            _currentLyrics = Lyrics.Parse(lyricsText);
            var currentLyrics = Lyrics.DisplayAt(_currentLyrics, lyricsText, TimeSpan.FromMilliseconds(_playback.Position));
            NowPlayingLyrics.Text = currentLyrics.Length == 0 && _currentLyrics.Lines.Count == 0 ? "Lyrics saved." : currentLyrics;
            if (backup is not null)
            {
                _store.RecordTagBackup(backup); await RefreshEditedTrackAsync(track.Path);
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

    private async void Queue_Click(object sender, RoutedEventArgs e)
    {
        CancelCrossfadeAndRestoreQueue();
        await ShowQueueAsync();
    }

    private async Task ShowQueueAsync()
    {
        var queue = new ListView { Height = 360, SelectionMode = ListViewSelectionMode.Single };
        AutomationProperties.SetName(queue, "Playback queue page");
        var body = new StackPanel { Spacing = 8, MinWidth = 360 };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var up = new Button { Content = "Move up" }; var down = new Button { Content = "Move down" };
        var remove = new Button { Content = "Remove" }; var clear = new Button { Content = "Clear upcoming" };
        var pageBack = new Button { Content = "Previous" }; var pageForward = new Button { Content = "Next" };
        var pageStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var queueStatus = new TextBlock { Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], TextWrapping = TextWrapping.Wrap };
        const int queuePageSize = 200;
        var queueOffset = _queueIndex >= 0 ? _queueIndex / queuePageSize * queuePageSize : 0;
        void RefreshQueue(int? selectIndex = null)
        {
            queueOffset = _queue.Count == 0 ? 0 : Math.Clamp(queueOffset, 0, ((_queue.Count - 1) / queuePageSize) * queuePageSize);
            var visible = _queue.Skip(queueOffset).Take(queuePageSize).Select((path, visibleIndex) =>
            {
                var index = queueOffset + visibleIndex;
                var track = _store.GetTrack(path);
                var label = track is null ? $"{Path.GetFileNameWithoutExtension(path)} · unavailable" : $"{track.Title} — {track.Artist}";
                return $"{(index == _queueIndex ? "Playing · " : "")}{index + 1}. {label}";
            }).ToArray();
            queue.ItemsSource = visible;
            var selected = selectIndex ?? _queueIndex;
            queue.SelectedIndex = selected >= queueOffset && selected < queueOffset + visible.Length ? selected - queueOffset : -1;
            pageStatus.Text = _queue.Count == 0 ? "Empty queue" : $"{queueOffset + 1:N0}–{Math.Min(queueOffset + queuePageSize, _queue.Count):N0} of {_queue.Count:N0}";
            pageBack.IsEnabled = queueOffset > 0;
            pageForward.IsEnabled = queueOffset + queuePageSize < _queue.Count;
        }
        void Move(int direction)
        {
            if (queue.SelectedIndex < 0) return;
            var index = queueOffset + queue.SelectedIndex; var next = index + direction;
            if (index < 0 || next < 0 || next >= _queue.Count) return;
            (_queue[index], _queue[next]) = (_queue[next], _queue[index]);
            if (_queueIndex == index) _queueIndex = next; else if (_queueIndex == next) _queueIndex = index;
            queueOffset = next / queuePageSize * queuePageSize;
            RefreshQueue(next); SaveSession();
        }
        pageBack.Click += (_, _) => { queueOffset = Math.Max(0, queueOffset - queuePageSize); RefreshQueue(); };
        pageForward.Click += (_, _) => { queueOffset = Math.Min(Math.Max(0, _queue.Count - 1), queueOffset + queuePageSize) / queuePageSize * queuePageSize; RefreshQueue(); };
        up.Click += (_, _) => Move(-1); down.Click += (_, _) => Move(1);
        remove.Click += (_, _) =>
        {
            if (queue.SelectedIndex < 0) return;
            var index = queueOffset + queue.SelectedIndex;
            if (index < 0 || index >= _queue.Count) return;
            if (index == _queueIndex) { queueStatus.Text = "The currently playing track cannot be removed from the queue."; return; }
            _queue.RemoveAt(index); if (index < _queueIndex) _queueIndex--;
            RefreshQueue(Math.Min(index, _queue.Count - 1)); SaveSession();
        };
        clear.Click += (_, _) =>
        {
            var current = _playback.CurrentTrack;
            _queue.Clear();
            if (current is not null) { _queue.Add(current.Path); _queueIndex = 0; } else _queueIndex = -1;
            queueOffset = 0; RefreshQueue(); SaveSession();
        };
        queue.DoubleTapped += (_, _) =>
        {
            var index = queueOffset + queue.SelectedIndex;
            if (index >= 0 && index < _queue.Count && ResolveQueueTrack(index) is { } track) PlayTrack(track, false, index);
            RefreshQueue();
        };
        controls.Children.Add(up); controls.Children.Add(down); controls.Children.Add(remove); controls.Children.Add(clear);
        controls.Children.Add(pageBack); controls.Children.Add(pageStatus); controls.Children.Add(pageForward);
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
                    else { _lastPlayCountPosition = _playback.Position; _playback.PlayLoaded(); SetPlayPauseVisual(true); }
                    if (_systemControls is not null) _systemControls.PlaybackStatus = MediaPlaybackStatus.Playing;
                    break;
                case SystemMediaTransportControlsButton.Pause: CancelCrossfadeAndRestoreQueue(); _playback.Pause(); SetPlayPauseVisual(false); if (_systemControls is not null) _systemControls.PlaybackStatus = MediaPlaybackStatus.Paused; break;
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
