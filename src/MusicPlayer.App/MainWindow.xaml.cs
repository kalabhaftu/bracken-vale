using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
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
using Microsoft.UI.Windowing;
using Windows.Graphics.Imaging;
using Windows.Media;
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
    private readonly LibraryQueryService _libraryQueries;
    private readonly PlaylistLibraryService _playlistLibrary;
    private readonly PlayerSettingsService _playerSettings;
    private readonly LibraryLocationService _libraryLocations;
    private readonly TrackMetadataService _trackMetadata;
    private readonly LibraryScanCoordinator _libraryScan;
    private readonly PlaybackQueueCoordinator _playbackQueue = new();
    private IReadOnlyList<string> _queue => _playbackQueue.Entries;
    private readonly SemaphoreSlim _externalFileActionGate = new(1, 1);
    private readonly PlaybackService _playback = new();
    private readonly PlaybackCommandCoordinator _playbackCommands;
    private SystemMediaTransportControls? _systemControls;
    private TrayIconService? _tray;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _shuffle { get => _playbackQueue.Shuffle; set => _playbackQueue.SetShuffle(value); }
    private bool _windowClosed;
    private bool _countedCurrentPlay;
    private DateTimeOffset? _scanStartedUtc;
    private string _scanCurrentPath = "";
    private int _scanFilesFound;
    private int _scanDirectoriesVisited;
    private long _heardMilliseconds;
    private long _lastPlayCountPosition;
    private bool _crossfadeInProgress;
    private string? _crossfadeFailureSource;
    private int? _crossfadeSourceQueueIndex;
    private int _queueIndex { get => _playbackQueue.CurrentIndex; set => _playbackQueue.SetCurrentIndex(value); }
    private DispatcherQueueTimer? _volumeSaveDebounce;
    private DispatcherQueueTimer? _noticeDismissTimer;
    private int? _pendingVolumeSetting;
    private int _persistedVolume;
    private int _crossfadeSeconds;
    private string _repeatMode { get => _playbackQueue.RepeatMode; set => _playbackQueue.SetRepeatMode(value); }
    private TimeSpan? _repeatA { get => _playbackQueue.RepeatA; set => _playbackQueue.SetAbRepeatPoints(value, _repeatB); }
    private TimeSpan? _repeatB { get => _playbackQueue.RepeatB; set => _playbackQueue.SetAbRepeatPoints(_repeatA, value); }
    private DateTime _lastSessionSave = DateTime.UtcNow;
    private WebViewBridge? _webBridge;

    public MainWindow()
    {
        InitializeComponent();
        _libraryQueries = new(_store, _appData);
        _playbackCommands = new(_playbackQueue, _playback, _store.GetTrack, GetCurrentQueuePaths);
        _playlistLibrary = new(_store);
        _playerSettings = new(_store);
        _libraryLocations = new(_store);
        _trackMetadata = new(_appData);
        _libraryScan = new(new LibraryIndexer(_store, Path.Combine(_appData, "Artwork")));
        Title = "Music Player";
        _volumeSaveDebounce = DispatcherQueue.CreateTimer();
        _volumeSaveDebounce.Interval = TimeSpan.FromMilliseconds(250);
        _volumeSaveDebounce.IsRepeating = false;
        _volumeSaveDebounce.Tick += (_, _) => PersistPendingVolumeSetting();
        var volume = int.TryParse(_store.GetSetting("volume"), out var savedVolume) ? Math.Clamp(savedVolume, 0, 100) : 75;
        _persistedVolume = volume;
        _crossfadeSeconds = ReadCrossfadeSeconds();
        _playback.Volume = volume;
        _playback.SelectAudioOutputDevice(_store.GetSetting("audio-output-device"));
        ApplyStoredEqualizer();
        _playback.TrackEnded += Playback_TrackEnded;
        _playback.CrossfadeCompleted += Playback_CrossfadeCompleted;
        _playback.CrossfadeFailed += Playback_CrossfadeFailed;
        _playback.PlaybackFailed += Playback_Failed;
        InitializeSystemMediaControls();
        ApplyTraySetting();
        _clock.Tick += Clock_Tick;
        _clock.Start();
        _noticeDismissTimer = DispatcherQueue.CreateTimer();
        _noticeDismissTimer.Interval = TimeSpan.FromSeconds(4);
        _noticeDismissTimer.IsRepeating = false;
        _noticeDismissTimer.Tick += (_, _) => AppNotice.IsOpen = false;
        Closed += MainWindow_Closed;
        ApplyStoredAppearance();
        RestoreSession();
        StartStartupScan();
        _ = CheckForUpdatesAsync(false);
        WebUi.Loaded += WebUi_Loaded;
    }

}
