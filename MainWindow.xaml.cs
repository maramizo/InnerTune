using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace InnerTune;

public partial class MainWindow : Window
{
    private readonly LibraryStore _store = new();
    private readonly ProviderService _provider = new();
    private readonly ProviderService _discoveryProvider = new(TimeSpan.FromSeconds(10));
    private readonly CodexAgent _agent = new();
    private readonly UpdateService _updates = new();
    private readonly LyricsService _lyrics;
    private readonly ObservableCollection<ChatMessage> _chat = [];
    private readonly ObservableCollection<VideoCandidate> _videoCandidates = [];
    private readonly PlayerService _player;
    private readonly DispatcherTimer _fileDebounce = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly DispatcherTimer _volumeSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly DispatcherTimer _playbackSaveTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly DispatcherTimer _miniQueueCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(12) };
    private LibraryData _library = new();
    private FileSystemWatcher? _watcher;
    private Forms.NotifyIcon? _tray;
    private bool _reallyClose;
    private bool _mini;
    private bool _pinned;
    private bool _spinnerActive;
    private bool _settingsLoaded;
    private bool _playbackRestoreComplete;
    private bool _playbackDirty;
    private bool _pointerOverNowPlaying;
    private bool _pointerOverMiniQueue;
    private bool _lyricsVisible;
    private bool _homeLoaded;
    private Rect _fullBounds;
    private DateTime _ignoreFilesUntil;
    private string? _artworkUrl;
    private string? _lyricsTrackId;
    private LyricsDocument? _lyricsDocument;
    private CancellationTokenSource? _lyricsCancel;
    private CancellationTokenSource? _homeCancel;
    private int _activeLyricsIndex = -1;
    private string? _homeMood;
    private string? _historyTrackId;
    private string? _continueArtworkUrl;
    private CollectionDetail? _collectionDetail;
    private CancellationTokenSource? _videoCancel;
    private CancellationTokenSource? _videoSearchCancel;
    private bool _videoActive;
    private bool _videoPlaying;
    private bool _videoLoading;
    private bool _videoUsesOwnAudio;
    private bool _videoShouldAutoplay;
    private bool _updatingSeekSlider;
    private string? _videoTrackId;
    private string? _videoChooserTrackId;
    private VideoSelection? _activeVideoSelection;
    private TimeSpan _videoStartPosition;
    private TimeSpan _videoNaturalDuration;
    private bool _checkingUpdates;
    private string? _offeredUpdateTag;

    public MainWindow()
    {
        InitializeComponent();
        _lyrics = new LyricsService(_store.DataDirectory);
        _player = new PlayerService(_provider);
        _player.StateChanged += (_, _) => Dispatcher.Invoke(() => { UpdatePlayerUi(); MarkPlaybackDirty(); });
        _player.Failed += (_, message) => Dispatcher.Invoke(() => SetStatus(message, true));
        _agent.Activity += (_, activity) => Dispatcher.Invoke(() => AddAgentActivity(activity));
        ChatMessages.ItemsSource = _chat;
        VideoCandidateList.ItemsSource = _videoCandidates;
        SeekSlider.ValueChanged += SeekSlider_ValueChanged;
        _chat.Add(new() { Role = "Luna", Text = "Hi — ask me to find music, build a queue, organize your library, or control playback." });
        _fileDebounce.Tick += async (_, _) => { _fileDebounce.Stop(); await ReloadExternalChangesAsync(); };
        _volumeSaveTimer.Tick += async (_, _) => { _volumeSaveTimer.Stop(); await SaveAsync(); };
        _playbackSaveTimer.Tick += async (_, _) =>
        {
            _playbackSaveTimer.Stop();
            if (_playbackDirty || PlaybackIsPlaying || PlaybackIsLoading) await PersistPlaybackStateAsync();
            if (!_reallyClose) { _playbackSaveTimer.Interval = TimeSpan.FromSeconds(10); _playbackSaveTimer.Start(); }
        };
        _miniQueueCloseTimer.Tick += (_, _) => { _miniQueueCloseTimer.Stop(); if (!_pointerOverNowPlaying && !_pointerOverMiniQueue) MiniQueuePopup.IsOpen = false; };
        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); StatusPill.Visibility = Visibility.Collapsed; };
        _updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync(false);
        Loaded += MainWindow_Loaded;
        SizeChanged += (_, _) => ApplyPlayerBarLayout();
        IsVisibleChanged += (_, _) => { _player.SetUiUpdates(IsVisible); if (!IsVisible) { if (_videoActive) CloseVideo(); CloseVideoChooser(); CloseMiniQueue(); TrimWorkingSet(); } };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _library = await _store.LoadAsync();
        _library.Playback ??= new PlaybackSnapshot();
        _library.RecentlyPlayed ??= [];
        _library.VideoMappings ??= [];
        if (string.IsNullOrWhiteSpace(_library.QueueSourceName)) _library.QueueSourceName = "Current queue";
        var savedVolume = AppRuntime.AudibleVolume(_library.Volume);
        if (AppRuntime.IsTestMode) _library.Volume = 0;
        VolumeSlider.Value = savedVolume;
        _player.Volume = savedVolume;
        ApplyPlaybackModes();
        _settingsLoaded = true;
        BindLibrary();
        if (!AppRuntime.IsTestMode) CreateTrayIcon();
        Directory.CreateDirectory(_store.DataDirectory);
        _watcher = new FileSystemWatcher(_store.DataDirectory, "library.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName, EnableRaisingEvents = true };
        _watcher.Changed += LibraryFileChanged;
        _watcher.Created += LibraryFileChanged;
        _watcher.Renamed += LibraryFileChanged;
        await RestorePlaybackAsync();
        AppRuntime.TestLog($"loaded track={_player.CurrentTrack?.Id ?? "none"} status={_library.Playback.Status}");
        _playbackRestoreComplete = true;
        MarkPlaybackDirty();
        _playbackSaveTimer.Start();
        SetMode("home");
        if (!AppRuntime.IsTestMode)
        {
            _updateTimer.Start();
            _ = CheckForUpdatesAsync(false);
        }
    }

    private void CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open InnerTune", null, (_, _) => ShowAndActivate());
        menu.Items.Add("Mini player", null, (_, _) => Dispatcher.Invoke(() => { ShowAndActivate(); if (!_mini) ToggleMini(); }));
        menu.Items.Add("Play / pause", null, (_, _) => Dispatcher.Invoke(TogglePlayback));
        menu.Items.Add("Next", null, async (_, _) => await Dispatcher.InvokeAsync(NextPlaybackAsync));
        menu.Items.Add("Check for updates", null, async (_, _) => await Dispatcher.InvokeAsync(async () => await CheckForUpdatesAsync(true)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(ExitApp));
        _tray = new Forms.NotifyIcon
        {
            Text = "InnerTune",
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };
        _tray.MouseClick += (_, args) => { if (args.Button == Forms.MouseButtons.Left) Dispatcher.Invoke(ShowAndActivate); };
    }

    public void ShowAndActivate()
    {
        Show();
        if (AppRuntime.IsTestMode) return;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true; Topmost = _mini || _pinned;
    }

    private async Task CheckForUpdatesAsync(bool userInitiated)
    {
        if (_checkingUpdates || AppRuntime.IsTestMode) return;
        _checkingUpdates = true;
        if (userInitiated) SetStatus("Checking for updates…");
        try
        {
            var update = await _updates.CheckAsync();
            if (update is null)
            {
                if (userInitiated) SetStatus("InnerTune is up to date");
                return;
            }
            if (!userInitiated && string.Equals(_offeredUpdateTag, update.Tag, StringComparison.OrdinalIgnoreCase)) return;
            SetStatus($"Downloading InnerTune {update.Version}…");
            var prepared = await _updates.DownloadAsync(update);
            SetStatus($"Installing InnerTune {update.Version}…");
            CapturePlaybackState();
            await _store.SaveAsync(_library);
            UpdateService.LaunchInstaller(prepared);
            _offeredUpdateTag = update.Tag;
        }
        catch (Exception error)
        {
            AppRuntime.TestLog($"update failed: {error}");
            SetStatus(userInitiated
                ? $"Update check failed: {error.Message}"
                : "Automatic update failed — InnerTune will retry later", true);
        }
        finally { _checkingUpdates = false; }
    }

    private async void LibraryFileChanged(object sender, FileSystemEventArgs e)
    {
        if (DateTime.UtcNow < _ignoreFilesUntil) return;
        await Dispatcher.InvokeAsync(() => { _fileDebounce.Stop(); _fileDebounce.Start(); });
    }

    private async Task ReloadExternalChangesAsync()
    {
        try
        {
            var updated = await _store.LoadAsync();
            var commands = updated.PendingCommands.ToList();
            updated.PendingCommands.Clear();
            _library = updated;
            _library.Playback ??= new PlaybackSnapshot();
            _library.RecentlyPlayed ??= [];
            _library.VideoMappings ??= [];
            if (string.IsNullOrWhiteSpace(_library.QueueSourceName)) _library.QueueSourceName = "Current queue";
            ApplyPlaybackModes();
            BindLibrary();
            if (commands.Count > 0)
            {
                await SaveAsync();
                foreach (var command in commands) await ExecuteCommandAsync(command);
            }
        }
        catch (IOException) { _fileDebounce.Start(); }
        catch (Exception e) { SetStatus(e.Message, true); }
    }

    private async Task ExecuteCommandAsync(PlaybackCommand command)
    {
        switch (command.Type)
        {
            case "play" when command.Index is not null: await _player.PlayAsync(command.Index.Value); break;
            case "pause" when PlaybackIsPlaying: TogglePlayback(); break;
            case "resume" when !PlaybackIsPlaying: TogglePlayback(); break;
            case "toggle": TogglePlayback(); break;
            case "next": await NextPlaybackAsync(); break;
            case "previous": await PreviousPlaybackAsync(); break;
            case "seek" when command.Seconds is not null: SeekPlayback(PlaybackPosition.TotalSeconds + command.Seconds.Value); break;
            case "volume" when command.Value is not null: VolumeSlider.Value = Math.Clamp(command.Value.Value, 0, 100); break;
            case "shuffle" when command.Enabled is not null:
                _library.ShuffleEnabled = command.Enabled.Value; _player.ShuffleEnabled = command.Enabled.Value; await SaveAsync(); break;
            case "repeat" when command.Mode is not null:
                _library.RepeatMode = command.Mode;
                _player.RepeatMode = command.Mode.ToLowerInvariant() switch { "all" => PlaybackRepeatMode.All, "one" => PlaybackRepeatMode.One, _ => PlaybackRepeatMode.Off };
                await SaveAsync(); break;
            case "watch_video": await OpenVideoAsync(); break;
            case "close_video": CloseVideo(); break;
        }
    }

    private void BindLibrary()
    {
        for (var i = 0; i < _library.Queue.Count; i++) { _library.Queue[i].Number = (i + 1).ToString("00"); _library.Queue[i].Refresh(); }
        QueueList.ItemsSource = _library.Queue;
        MiniQueueList.ItemsSource = _library.Queue;
        foreach (var saved in _library.SavedQueues) saved.DisplayPath = JoinFolderPath(saved.FolderId, saved.Name);
        foreach (var favorite in _library.Favorites) favorite.FolderPath = JoinFolderPath(favorite.FolderId, "Favorites");
        SavedQueueList.ItemsSource = _library.SavedQueues;
        FavoritesList.ItemsSource = _library.Favorites;
        RefreshFavoriteStates();
        FavoritesEmpty.Visibility = _library.Favorites.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FavoritesList.Visibility = _library.Favorites.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        SavedQueuesEmpty.Visibility = _library.SavedQueues.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SavedQueueList.Visibility = _library.SavedQueues.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        QueueCountText.Text = $"{_library.Queue.Count} {(_library.Queue.Count == 1 ? "song" : "songs")}";
        MiniQueueCount.Text = $"{_library.Queue.Count} {(_library.Queue.Count == 1 ? "song" : "songs")}";
        QueueEmpty.Visibility = _library.Queue.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        QueueList.Visibility = _library.Queue.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        QueueActions.Visibility = _library.Queue.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _player.SetQueue(_library.Queue);
        UpdatePlayerUi();
    }

    private string JoinFolderPath(string? folderId, string leaf)
    {
        var parts = new List<string> { leaf };
        var seen = new HashSet<string>();
        var current = _library.Folders.FirstOrDefault(x => x.Id == folderId);
        while (current is not null && seen.Add(current.Id))
        {
            parts.Insert(0, current.Name);
            current = _library.Folders.FirstOrDefault(x => x.Id == current.ParentId);
        }
        return string.Join(" / ", parts);
    }

    private async Task SaveAsync()
    {
        CapturePlaybackState();
        _ignoreFilesUntil = DateTime.UtcNow.AddMilliseconds(500);
        await _store.SaveAsync(_library);
        BindLibrary();
    }

    private void MarkQueueEdited()
    {
        _library.QueueSourceId = null;
        _library.QueueSourceName = "Current queue";
        _playbackDirty = true;
    }

    private async Task RestorePlaybackAsync()
    {
        var saved = _library.Playback;
        if (saved is null || saved.Status == "idle" || string.IsNullOrWhiteSpace(saved.TrackId)) return;
        var index = _library.Queue.ToList().FindIndex(track => track.Id == saved.TrackId);
        var track = index >= 0 ? _library.Queue[index] : saved.Track;
        if (track is null) return;
        var position = TimeSpan.FromSeconds(Math.Max(0, saved.PositionSeconds));
        if (saved.Status.Equals("playing", StringComparison.OrdinalIgnoreCase))
            await _player.RestorePlayingAsync(track, index, position);
        else
            _player.RestorePaused(track, index, position);
    }

    private void MarkPlaybackDirty()
    {
        if (!_playbackRestoreComplete) return;
        var saved = _library.Playback;
        var track = _player.CurrentTrack;
        var status = track is null ? "idle" : (PlaybackIsPlaying || PlaybackIsLoading ? "playing" : "paused");
        var identityChanged = saved.TrackId != track?.Id || saved.Status != status ||
            saved.QueueId != _library.QueueSourceId || saved.QueueName != _library.QueueSourceName;
        var positionChanged = Math.Abs(saved.PositionSeconds - PlaybackStoragePosition.TotalSeconds) >= 2;
        if (identityChanged || positionChanged)
        {
            _playbackDirty = true;
            if (identityChanged || (positionChanged && !PlaybackIsPlaying && !PlaybackIsLoading))
            {
                _playbackSaveTimer.Stop();
                _playbackSaveTimer.Interval = TimeSpan.FromMilliseconds(500);
                _playbackSaveTimer.Start();
            }
        }
    }

    private void CapturePlaybackState()
    {
        if (!_playbackRestoreComplete) return;
        var track = _player.CurrentTrack;
        _library.Playback = new PlaybackSnapshot
        {
            Status = track is null ? "idle" : (PlaybackIsPlaying || PlaybackIsLoading ? "playing" : "paused"),
            Track = track,
            TrackId = track?.Id,
            QueueIndex = _player.CurrentIndex,
            QueueId = _library.QueueSourceId,
            QueueName = string.IsNullOrWhiteSpace(_library.QueueSourceName) ? "Current queue" : _library.QueueSourceName,
            PositionSeconds = track is null ? 0 : PlaybackStoragePosition.TotalSeconds,
            UpdatedAt = DateTimeOffset.Now
        };
        _playbackDirty = false;
    }

    private async Task PersistPlaybackStateAsync()
    {
        CapturePlaybackState();
        _ignoreFilesUntil = DateTime.UtcNow.AddMilliseconds(500);
        try { await _store.SaveAsync(_library); }
        catch (Exception e) { SetStatus(e.Message, true); }
    }

    private void SetMode(string mode)
    {
        HomePanel.Visibility = mode == "home" ? Visibility.Visible : Visibility.Collapsed;
        SearchPanel.Visibility = mode == "search" ? Visibility.Visible : Visibility.Collapsed;
        AiPanel.Visibility = mode == "ai" ? Visibility.Visible : Visibility.Collapsed;
        LibraryPanel.Visibility = mode == "library" ? Visibility.Visible : Visibility.Collapsed;
        foreach (var pair in new[] { (HomeNav, "home"), (SearchNav, "search"), (AiNav, "ai"), (FavoritesNav, "favorites"), (SavedNav, "saved") })
        {
            var selected = pair.Item2 == mode || (mode == "library" && pair.Item2 == (LibraryTabs.SelectedIndex == 0 ? "favorites" : "saved"));
            pair.Item1.Background = selected
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(48, 42, 66))
                : System.Windows.Media.Brushes.Transparent;
            pair.Item1.Foreground = selected ? System.Windows.Media.Brushes.White :
                new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(184, 182, 191));
        }
        if (mode == "search") { SearchBox.Focus(); SearchBox.SelectAll(); }
        if (mode == "ai") { AiBox.Focus(); ChatScroll.ScrollToEnd(); }
        if (mode == "home" && !_homeLoaded) _ = LoadHomeAsync();
    }

    private async Task SearchAsync()
    {
        var query = SearchBox.Text.Trim();
        if (query.Length == 0) return;
        SetStatus("Searching…");
        SearchBox.IsEnabled = false;
        try
        {
            var results = await _provider.SearchAsync(query);
            SearchResults.ItemsSource = results;
            RefreshFavoriteStates();
            SearchEmpty.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SetStatus(results.Count == 0 ? "No songs found" : $"Found {results.Count} songs");
        }
        catch (Exception e) { SetStatus(e.Message, true); }
        finally { SearchBox.IsEnabled = true; }
    }

    private async Task SendToAiAsync()
    {
        var request = AiBox.Text.Trim();
        if (request.Length == 0 || _agent.IsRunning) return;
        AiBox.Clear();
        _chat.Add(new() { Role = "You", Text = request });
        AiSendButton.IsEnabled = false;
        ChatScroll.ScrollToEnd();
        SetStatus("Luna is working…");
        try
        {
            await _agent.RunAsync(request, _store.DataDirectory);
            await ReloadExternalChangesAsync();
            _statusTimer.Stop();
            StatusPill.Visibility = Visibility.Collapsed;
        }
        catch (Exception e) { _chat.Add(new() { Role = "Error", Text = e.Message }); SetStatus("AI request failed", true); }
        finally { AiSendButton.IsEnabled = true; AiBox.Focus(); ChatScroll.ScrollToEnd(); }
    }

    private void AddAgentActivity(AgentActivity activity)
    {
        _chat.Add(new() { Role = activity.Role, Text = activity.Text });
        ChatScroll.ScrollToEnd();
        if (activity.Role == "Action") SetStatus("Luna is taking action…");
        else if (activity.Role == "Thinking") SetStatus("Luna is thinking…");
        else if (activity.Role == "Error") SetStatus("AI action failed", true);
    }

    private void UpdatePlayerUi()
    {
        var track = _player.CurrentTrack;
        RecordListeningHistory(track);
        UpdateContinueListening(track);
        UpdateLyricsTrack(track);
        NowTitle.Text = track?.Title ?? "Nothing playing";
        NowArtist.Text = track?.Artist ?? "Choose a song from the queue";
        if (_artworkUrl != track?.ArtworkUrl)
        {
            _artworkUrl = track?.ArtworkUrl;
            try { NowArtwork.Source = string.IsNullOrWhiteSpace(_artworkUrl) ? null : new BitmapImage(new Uri(_artworkUrl)); }
            catch { NowArtwork.Source = null; }
        }
        if (PlaybackIsLoading)
        {
            PlayGlyph.Text = "\uE895";
            if (!_spinnerActive)
            {
                _spinnerActive = true;
                PlayGlyphRotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty,
                    new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(750)) { RepeatBehavior = RepeatBehavior.Forever });
            }
        }
        else
        {
            if (_spinnerActive)
            {
                PlayGlyphRotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
                PlayGlyphRotation.Angle = 0;
                _spinnerActive = false;
            }
            PlayGlyph.Text = PlaybackIsPlaying ? "\uE769" : "\uE768";
        }
        var duration = PlaybackDuration.TotalSeconds;
        SeekSlider.Maximum = Math.Max(1, duration);
        if (!SeekSlider.IsMouseCaptureWithin)
        {
            _updatingSeekSlider = true;
            SeekSlider.Value = Math.Min(PlaybackPosition.TotalSeconds, SeekSlider.Maximum);
            _updatingSeekSlider = false;
        }
        PositionText.Text = FormatTime(PlaybackPosition);
        DurationText.Text = FormatTime(PlaybackDuration);
        WatchVideoButton.IsEnabled = track is not null;
        WatchVideoButton.Foreground = _videoActive ? (System.Windows.Media.Brush)FindResource("Accent") : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(169, 165, 177));
        if (_tray is not null) _tray.Text = track is null ? "InnerTune" : $"InnerTune — {Truncate(track.Title, 48)}";
        if (MiniQueuePopup.IsOpen) MiniQueueList.SelectedIndex = _player.CurrentIndex;
        UpdatePlaybackModeButtons();
        UpdateLyricsPosition();
        SynchronizeVideoWithAudio(track);
    }

    private bool PlaybackIsPlaying => _videoActive && _videoUsesOwnAudio ? _videoPlaying : _player.IsPlaying;
    private bool PlaybackIsLoading => _videoActive && _videoUsesOwnAudio ? _videoLoading : _player.IsLoading;
    private TimeSpan PlaybackPosition
    {
        get
        {
            if (!_videoActive || !_videoUsesOwnAudio) return _player.Position;
            if (_videoLoading) return _videoStartPosition;
            try { return VideoPlayer.Position; }
            catch { return _videoStartPosition; }
        }
    }
    private TimeSpan PlaybackDuration => _videoActive && _videoUsesOwnAudio && _videoNaturalDuration > TimeSpan.Zero
        ? _videoNaturalDuration
        : _player.Duration;
    private TimeSpan PlaybackStoragePosition => _videoActive && _videoUsesOwnAudio
        ? MapPosition(PlaybackPosition, _videoNaturalDuration, _player.Duration)
        : _player.Position;

    private void TogglePlayback()
    {
        if (!_videoActive || !_videoUsesOwnAudio) { _player.Toggle(); return; }
        if (_videoLoading)
        {
            _videoShouldAutoplay = !_videoShouldAutoplay;
            UpdatePlayerUi();
            return;
        }
        if (_videoPlaying) { VideoPlayer.Pause(); _videoPlaying = false; }
        else { VideoPlayer.Play(); _videoPlaying = true; }
        _videoShouldAutoplay = _videoPlaying;
        UpdatePlayerUi();
        MarkPlaybackDirty();
    }

    private void SeekPlayback(double seconds)
    {
        var clamped = Math.Clamp(seconds, 0, Math.Max(0, PlaybackDuration.TotalSeconds));
        if (_videoActive && _videoUsesOwnAudio)
        {
            _videoStartPosition = TimeSpan.FromSeconds(clamped);
            if (!_videoLoading) try { VideoPlayer.Position = _videoStartPosition; } catch { }
            UpdatePlayerUi();
            MarkPlaybackDirty();
            return;
        }
        _player.Seek(clamped);
        if (_videoActive && !_videoLoading && _videoTrackId == _player.CurrentTrack?.Id)
            try { VideoPlayer.Position = TimeSpan.FromSeconds(clamped); } catch { }
    }

    private async Task NextPlaybackAsync()
    {
        if (_videoActive && _videoUsesOwnAudio)
        {
            try { VideoPlayer.Pause(); } catch { }
            _videoPlaying = false;
            _videoUsesOwnAudio = false;
        }
        await _player.NextAsync();
    }

    private async Task PreviousPlaybackAsync()
    {
        if (_videoActive && _videoUsesOwnAudio)
        {
            if (PlaybackPosition.TotalSeconds > 4) { SeekPlayback(0); return; }
            try { VideoPlayer.Pause(); } catch { }
            _videoPlaying = false;
            _videoUsesOwnAudio = false;
            _player.Seek(0);
        }
        await _player.PreviousAsync();
    }

    private async void WatchVideo_Click(object sender, RoutedEventArgs e)
    {
        AppRuntime.TestLog($"watch invoked track={_player.CurrentTrack?.Id ?? "none"} active={_videoActive}");
        if (_videoActive) { CloseVideo(); return; }
        await OpenVideoAsync();
    }

    private async Task OpenVideoAsync()
    {
        if (_videoActive) return;
        if (_player.CurrentTrack is not { } track) return;
        if (_library.VideoMappings.TryGetValue(track.Id, out var saved) && !string.IsNullOrWhiteSpace(saved.VideoId))
        {
            _videoActive = true;
            await LoadVideoForTrackAsync(track, saved);
            return;
        }
        await SearchVideoCandidatesAsync(track, null, true);
    }

    private async Task SearchVideoCandidatesAsync(Track track, string? query, bool autoUseBest)
    {
        AppRuntime.TestLog($"video search started track={track.Id} automatic={autoUseBest}");
        _videoSearchCancel?.Cancel();
        _videoSearchCancel?.Dispose();
        var cancellation = new CancellationTokenSource();
        _videoSearchCancel = cancellation;
        _videoChooserTrackId = track.Id;
        VideoChooserSubtitle.Text = $"{track.Title}  ·  {track.Artist}";
        if (string.IsNullOrWhiteSpace(query)) VideoSearchBox.Text = $"{track.Artist} {track.Title} official music video";
        VideoChooserPanel.Visibility = Visibility.Visible;
        VideoChooserLoading.Visibility = Visibility.Visible;
        VideoChooserEmpty.Visibility = Visibility.Collapsed;
        VideoCandidateList.Visibility = Visibility.Collapsed;
        _videoCandidates.Clear();
        try
        {
            var candidates = await _discoveryProvider.FindVideoCandidatesAsync(track, query, cancellation.Token);
            AppRuntime.TestLog($"video search returned track={track.Id} candidates={candidates.Count}");
            if (cancellation.IsCancellationRequested || _videoChooserTrackId != track.Id || _player.CurrentTrack?.Id != track.Id) return;
            foreach (var candidate in candidates) _videoCandidates.Add(candidate);
            VideoChooserLoading.Visibility = Visibility.Collapsed;
            if (_videoCandidates.Count == 0)
            {
                VideoChooserEmpty.Visibility = Visibility.Visible;
                return;
            }
            if (autoUseBest && _videoCandidates[0].Score >= 75)
            {
                AppRuntime.TestLog($"video auto-select id={_videoCandidates[0].Id} score={_videoCandidates[0].Score}");
                await UseVideoCandidateAsync(track, _videoCandidates[0]);
                return;
            }
            VideoCandidateList.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            AppRuntime.TestLog($"video search failed: {error}");
            if (cancellation.IsCancellationRequested) return;
            VideoChooserLoading.Visibility = Visibility.Collapsed;
            VideoChooserEmpty.Visibility = Visibility.Visible;
            SetStatus(error.Message, true);
        }
    }

    private async Task UseVideoCandidateAsync(Track track, VideoCandidate candidate)
    {
        AppRuntime.TestLog($"video selected track={track.Id} video={candidate.Id} kind={candidate.Kind}");
        var selection = VideoSelection.FromCandidate(candidate);
        _library.VideoMappings[track.Id] = selection;
        await SaveAsync();
        CloseVideoChooser();
        _videoActive = true;
        await LoadVideoForTrackAsync(track, selection);
    }

    private async Task<VideoSelection?> FindAutomaticVideoAsync(Track track, CancellationToken token)
    {
        if (_library.VideoMappings.TryGetValue(track.Id, out var saved) && !string.IsNullOrWhiteSpace(saved.VideoId)) return saved;
        var candidates = await _discoveryProvider.FindVideoCandidatesAsync(track, null, token);
        var best = candidates.FirstOrDefault(candidate => candidate.Score >= 65);
        if (best is null) return null;
        var selection = VideoSelection.FromCandidate(best);
        _library.VideoMappings[track.Id] = selection;
        await SaveAsync();
        return selection;
    }

    private async Task LoadVideoForTrackAsync(Track track, VideoSelection? preferred = null)
    {
        AppRuntime.TestLog($"video load started track={track.Id} preferred={preferred?.VideoId ?? "automatic"}");
        _videoCancel?.Cancel();
        _videoCancel?.Dispose();
        var cancellation = new CancellationTokenSource();
        _videoCancel = cancellation;
        _videoTrackId = track.Id;
        _videoPlaying = false;
        _videoLoading = true;
        _videoUsesOwnAudio = false;
        _videoNaturalDuration = TimeSpan.Zero;
        _activeVideoSelection = null;
        VideoTitle.Text = track.Title;
        VideoMeta.Text = "Finding a real video…";
        VideoLoading.Visibility = Visibility.Visible;
        VideoPanel.Visibility = Visibility.Visible;
        try { VideoPlayer.Stop(); VideoPlayer.Source = null; } catch { }
        UpdatePlayerUi();
        try
        {
            var selection = preferred ?? await FindAutomaticVideoAsync(track, cancellation.Token);
            if (cancellation.IsCancellationRequested || !_videoActive || _videoTrackId != track.Id || _player.CurrentTrack?.Id != track.Id) return;
            if (selection is null)
            {
                SetStatus("No convincing music video was found for this song.");
                CloseVideo();
                return;
            }
            _activeVideoSelection = selection;
            _videoUsesOwnAudio = selection.UseVideoAudio;
            _videoStartPosition = _player.Position;
            _videoShouldAutoplay = _player.IsPlaying || _player.IsLoading;
            if (_videoUsesOwnAudio) _player.Pause();
            VideoTitle.Text = selection.Title;
            VideoMeta.Text = $"{selection.Author}  ·  {selection.Kind}";
            var video = await _discoveryProvider.ResolveVideoAsync(selection.VideoId, cancellation.Token);
            AppRuntime.TestLog($"video stream resolved id={selection.VideoId} quality={video.Quality}");
            if (cancellation.IsCancellationRequested || !_videoActive || _videoTrackId != track.Id || _player.CurrentTrack?.Id != track.Id) return;
            if (!string.IsNullOrWhiteSpace(video.Quality)) VideoMeta.Text += $"  ·  {video.Quality}";
            VideoPlayer.IsMuted = !_videoUsesOwnAudio;
            VideoPlayer.Volume = AppRuntime.AudibleVolume(_library.Volume) / 100;
            VideoPlayer.Source = new Uri(video.Url);
            VideoPlayer.Play();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            AppRuntime.TestLog($"video load failed: {error}");
            if (!cancellation.IsCancellationRequested)
            {
                SetStatus(error.Message, true);
                CloseVideo();
            }
        }
    }

    private void VideoPlayer_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (!_videoActive || _videoTrackId != _player.CurrentTrack?.Id) return;
        _videoNaturalDuration = VideoPlayer.NaturalDuration.HasTimeSpan
            ? VideoPlayer.NaturalDuration.TimeSpan
            : TimeSpan.FromSeconds(Math.Max(0, _activeVideoSelection?.DurationSeconds ?? 0));
        var start = _videoUsesOwnAudio
            ? MapPosition(_videoStartPosition, _player.Duration, _videoNaturalDuration)
            : _player.Position;
        VideoPlayer.Position = TimeSpan.FromSeconds(Math.Clamp(start.TotalSeconds, 0, Math.Max(0, _videoNaturalDuration.TotalSeconds)));
        _videoLoading = false;
        VideoLoading.Visibility = Visibility.Collapsed;
        if ((_videoUsesOwnAudio && _videoShouldAutoplay) || (!_videoUsesOwnAudio && _player.IsPlaying)) { VideoPlayer.Play(); _videoPlaying = true; }
        else { VideoPlayer.Pause(); _videoPlaying = false; }
        UpdatePlayerUi();
    }

    private async void VideoPlayer_MediaEnded(object sender, RoutedEventArgs e)
    {
        if (!_videoActive) return;
        _videoPlaying = false;
        if (!_videoUsesOwnAudio) return;
        if (_player.RepeatMode == PlaybackRepeatMode.One)
        {
            VideoPlayer.Position = TimeSpan.Zero;
            VideoPlayer.Play();
            _videoPlaying = true;
            return;
        }
        var endedTrackId = _player.CurrentTrack?.Id;
        _videoUsesOwnAudio = false;
        await _player.NextAsync(true);
        if (_player.CurrentTrack?.Id == endedTrackId)
        {
            _player.Seek(_player.Duration.TotalSeconds);
            CloseVideo();
        }
    }

    private void VideoPlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (!_videoActive) return;
        SetStatus($"Couldn’t play this video: {e.ErrorException?.Message ?? "unknown media error"}", true);
        CloseVideo();
    }

    private void CloseVideo_Click(object sender, RoutedEventArgs e) => CloseVideo();

    private void CloseVideo()
    {
        if (!_videoActive) return;
        var usedOwnAudio = _videoUsesOwnAudio;
        var position = PlaybackPosition;
        var shouldResume = usedOwnAudio && (_videoPlaying || (_videoLoading && _videoShouldAutoplay));
        var mappedPosition = usedOwnAudio ? MapPosition(position, _videoNaturalDuration, _player.Duration) : _player.Position;
        _videoActive = false;
        _videoPlaying = false;
        _videoLoading = false;
        _videoUsesOwnAudio = false;
        _videoShouldAutoplay = false;
        _videoTrackId = null;
        _activeVideoSelection = null;
        _videoNaturalDuration = TimeSpan.Zero;
        _videoCancel?.Cancel();
        _videoCancel?.Dispose();
        _videoCancel = null;
        try { VideoPlayer.Stop(); VideoPlayer.Source = null; } catch { }
        VideoPanel.Visibility = Visibility.Collapsed;
        CloseVideoChooser();
        if (usedOwnAudio)
        {
            _player.Seek(mappedPosition.TotalSeconds);
            if (shouldResume && !_player.IsPlaying) _player.Toggle();
        }
        UpdatePlayerUi();
    }

    private void SynchronizeVideoWithAudio(Track? track)
    {
        if (!_videoActive) return;
        if (track is null) { CloseVideo(); return; }
        if (_videoTrackId != track.Id)
        {
            _ = LoadVideoForTrackAsync(track);
            return;
        }
        if (_videoLoading) return;
        if (_videoUsesOwnAudio) return;
        try
        {
            var drift = Math.Abs((VideoPlayer.Position - _player.Position).TotalMilliseconds);
            if (drift > 650) VideoPlayer.Position = _player.Position;
            if (_player.IsPlaying && !_videoPlaying) { VideoPlayer.Play(); _videoPlaying = true; }
            else if (!_player.IsPlaying && _videoPlaying) { VideoPlayer.Pause(); _videoPlaying = false; }
        }
        catch { }
    }

    private static TimeSpan MapPosition(TimeSpan position, TimeSpan sourceDuration, TimeSpan targetDuration)
    {
        if (sourceDuration.TotalSeconds <= 0 || targetDuration.TotalSeconds <= 0) return position;
        var progress = Math.Clamp(position.TotalSeconds / sourceDuration.TotalSeconds, 0, 1);
        return TimeSpan.FromSeconds(targetDuration.TotalSeconds * progress);
    }

    private async void ChangeVideo_Click(object sender, RoutedEventArgs e)
    {
        if (_player.CurrentTrack is { } track) await SearchVideoCandidatesAsync(track, null, false);
    }

    private async void VideoSearch_Click(object sender, RoutedEventArgs e)
    {
        if (_player.CurrentTrack is { } track) await SearchVideoCandidatesAsync(track, VideoSearchBox.Text, false);
    }

    private async void VideoSearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (_player.CurrentTrack is { } track) await SearchVideoCandidatesAsync(track, VideoSearchBox.Text, false);
    }

    private async void UseVideoCandidate_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not VideoCandidate candidate ||
            _player.CurrentTrack is not { } track || track.Id != _videoChooserTrackId) return;
        await UseVideoCandidateAsync(track, candidate);
    }

    private void CloseVideoChooser_Click(object sender, RoutedEventArgs e) => CloseVideoChooser();

    private void CloseVideoChooser()
    {
        _videoSearchCancel?.Cancel();
        _videoSearchCancel?.Dispose();
        _videoSearchCancel = null;
        _videoChooserTrackId = null;
        VideoChooserPanel.Visibility = Visibility.Collapsed;
    }

    private void ApplyPlaybackModes()
    {
        _player.ShuffleEnabled = _library.ShuffleEnabled;
        _player.RepeatMode = _library.RepeatMode.ToLowerInvariant() switch
        {
            "all" => PlaybackRepeatMode.All,
            "one" => PlaybackRepeatMode.One,
            _ => PlaybackRepeatMode.Off
        };
        UpdatePlaybackModeButtons();
    }

    private void UpdatePlaybackModeButtons()
    {
        var accent = (System.Windows.Media.Brush)FindResource("Accent");
        ShuffleButton.Foreground = _player.ShuffleEnabled ? accent : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(143, 139, 151));
        ShuffleButton.ToolTip = _player.ShuffleEnabled ? "Shuffle on" : "Shuffle off";
        RepeatButton.Foreground = _player.RepeatMode == PlaybackRepeatMode.Off ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(143, 139, 151)) : accent;
        RepeatButton.Content = _player.RepeatMode == PlaybackRepeatMode.One ? "\uE8ED" : "\uE8EE";
        RepeatButton.ToolTip = _player.RepeatMode switch { PlaybackRepeatMode.One => "Repeat this song", PlaybackRepeatMode.All => "Repeat queue", _ => "Repeat off" };
    }

    private void UpdateLyricsTrack(Track? track)
    {
        if (_lyricsTrackId == track?.Id) return;
        _lyricsTrackId = track?.Id;
        _lyricsCancel?.Cancel();
        _lyricsCancel?.Dispose();
        _lyricsCancel = null;
        _lyricsDocument = null;
        _activeLyricsIndex = -1;
        LyricsList.ItemsSource = null;
        LyricsList.Visibility = Visibility.Collapsed;
        LyricsLoading.Visibility = Visibility.Collapsed;
        LyricsEmpty.Visibility = Visibility.Visible;
        RetryLyricsButton.Visibility = Visibility.Collapsed;
        LyricsHint.Visibility = Visibility.Collapsed;
        if (track is null)
        {
            LyricsMeta.Text = "Play a song to see its lyrics";
            LyricsEmptyTitle.Text = "Nothing playing";
            LyricsEmptyDetail.Text = "Choose a song, then come back here.";
            return;
        }
        LyricsMeta.Text = $"{track.Title} · {track.Artist}";
        LyricsEmptyTitle.Text = "Ready when you are";
        LyricsEmptyDetail.Text = "Lyrics load when this view is open.";
        if (_lyricsVisible) _ = LoadLyricsAsync(track);
    }

    private async Task LoadLyricsAsync(Track track, bool refresh = false)
    {
        _lyricsCancel?.Cancel();
        _lyricsCancel?.Dispose();
        var cancellation = new CancellationTokenSource();
        _lyricsCancel = cancellation;
        _lyricsTrackId = track.Id;
        _lyricsDocument = null;
        _activeLyricsIndex = -1;
        LyricsEmpty.Visibility = Visibility.Collapsed;
        LyricsList.Visibility = Visibility.Collapsed;
        LyricsLoading.Visibility = Visibility.Visible;
        LyricsHint.Visibility = Visibility.Collapsed;
        RefreshLyricsButton.IsEnabled = false;
        try
        {
            var document = await _lyrics.GetAsync(track, refresh, cancellation.Token);
            if (cancellation.IsCancellationRequested || _player.CurrentTrack?.Id != track.Id) return;
            _lyricsDocument = document;
            LyricsMeta.Text = $"{document.TrackName} · {document.ArtistName}";
            LyricsLoading.Visibility = Visibility.Collapsed;
            if (document.IsInstrumental)
            {
                ShowLyricsEmpty("Instrumental", "There aren’t any words in this track.", false);
                return;
            }
            if (!document.Found || document.Lines.Count == 0)
            {
                ShowLyricsEmpty("No lyrics found", "Try again later, or refresh if this recording was matched incorrectly.", true);
                return;
            }
            LyricsList.ItemsSource = document.Lines;
            LyricsList.Visibility = Visibility.Visible;
            LyricsHint.Visibility = document.IsSynced ? Visibility.Visible : Visibility.Collapsed;
            UpdateLyricsPosition();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!cancellation.IsCancellationRequested)
                ShowLyricsEmpty("Couldn’t load lyrics", error.Message, true);
        }
        finally
        {
            if (ReferenceEquals(_lyricsCancel, cancellation)) RefreshLyricsButton.IsEnabled = true;
        }
    }

    private void ShowLyricsEmpty(string title, string detail, bool canRetry)
    {
        LyricsLoading.Visibility = Visibility.Collapsed;
        LyricsList.Visibility = Visibility.Collapsed;
        LyricsEmptyTitle.Text = title;
        LyricsEmptyDetail.Text = detail;
        RetryLyricsButton.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
        LyricsEmpty.Visibility = Visibility.Visible;
        LyricsHint.Visibility = Visibility.Collapsed;
    }

    private void UpdateLyricsPosition()
    {
        if (!_lyricsVisible || _lyricsDocument is not { IsSynced: true } document || document.Lines.Count == 0) return;
        var position = PlaybackPosition.TotalSeconds;
        var nextIndex = -1;
        for (var index = 0; index < document.Lines.Count; index++)
        {
            if (document.Lines[index].StartSeconds > position + .05) break;
            nextIndex = index;
        }
        if (nextIndex == _activeLyricsIndex) return;
        if (_activeLyricsIndex >= 0 && _activeLyricsIndex < document.Lines.Count) document.Lines[_activeLyricsIndex].IsActive = false;
        _activeLyricsIndex = nextIndex;
        if (nextIndex < 0) return;
        document.Lines[nextIndex].IsActive = true;
        LyricsList.ScrollIntoView(document.Lines[nextIndex]);
        Dispatcher.BeginInvoke(() => KeepActiveLyricComfortablyVisible(nextIndex), DispatcherPriority.Loaded);
    }

    private void KeepActiveLyricComfortablyVisible(int index)
    {
        if (LyricsList.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem item) return;
        var scroll = FindVisualChild<ScrollViewer>(LyricsList);
        if (scroll is null || scroll.ViewportHeight <= 0) return;
        var position = item.TransformToAncestor(scroll).Transform(new System.Windows.Point()).Y;
        if (position >= scroll.ViewportHeight * .2 && position <= scroll.ViewportHeight * .72) return;
        scroll.ScrollToVerticalOffset(Math.Max(0, scroll.VerticalOffset + position - scroll.ViewportHeight * .4));
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            if (FindVisualChild<T>(child) is { } descendant) return descendant;
        }
        return null;
    }

    private void QueueView_Click(object sender, RoutedEventArgs e) => SetSideView(false);
    private void LyricsView_Click(object sender, RoutedEventArgs e)
    {
        SetSideView(true);
        var track = _player.CurrentTrack;
        if (track is not null && (_lyricsDocument?.TrackId != track.Id || (!_lyricsDocument.Found && DateTimeOffset.UtcNow - _lyricsDocument.FetchedAt > TimeSpan.FromDays(1))))
            _ = LoadLyricsAsync(track);
    }

    private void SetSideView(bool lyrics)
    {
        _lyricsVisible = lyrics;
        QueuePanel.Visibility = lyrics ? Visibility.Collapsed : Visibility.Visible;
        LyricsPanel.Visibility = lyrics ? Visibility.Visible : Visibility.Collapsed;
        QueueViewButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(
            lyrics ? (byte)25 : (byte)57, lyrics ? (byte)25 : (byte)50, lyrics ? (byte)30 : (byte)71));
        LyricsViewButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(
            lyrics ? (byte)57 : (byte)25, lyrics ? (byte)50 : (byte)25, lyrics ? (byte)71 : (byte)30));
        if (lyrics) UpdateLyricsPosition();
    }

    private async void RefreshLyrics_Click(object sender, RoutedEventArgs e)
    {
        if (_player.CurrentTrack is { } track) await LoadLyricsAsync(track, true);
    }

    private void LyricsLine_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(LyricsList, source) is not ListBoxItem item || item.DataContext is not LyricsLine { StartSeconds: { } seconds }) return;
        _player.Seek(seconds);
        LyricsList.SelectedItem = null;
        e.Handled = true;
    }

    private void RecordListeningHistory(Track? track)
    {
        if (track is null || !PlaybackIsPlaying || _historyTrackId == track.Id || !_settingsLoaded) return;
        _historyTrackId = track.Id;
        var entry = _library.RecentlyPlayed.FirstOrDefault(item => item.Track.Id == track.Id);
        if (entry is null) entry = new HistoryEntry { Track = track };
        else
        {
            _library.RecentlyPlayed.Remove(entry);
            entry.Track = track;
            entry.PlayCount++;
            entry.LastPlayedAt = DateTimeOffset.Now;
        }
        _library.RecentlyPlayed.Insert(0, entry);
        while (_library.RecentlyPlayed.Count > 40) _library.RecentlyPlayed.RemoveAt(_library.RecentlyPlayed.Count - 1);
        _playbackDirty = true;
        _playbackSaveTimer.Stop();
        _playbackSaveTimer.Interval = TimeSpan.FromMilliseconds(500);
        _playbackSaveTimer.Start();
    }

    private void UpdateContinueListening(Track? track)
    {
        ContinueCard.Visibility = track is null ? Visibility.Collapsed : Visibility.Visible;
        if (track is null) return;
        ContinueTitle.Text = track.Title;
        ContinueArtist.Text = track.Artist;
        ContinueContext.Text = $"{_library.QueueSourceName}  ·  {FormatTime(PlaybackPosition)} of {FormatTime(PlaybackDuration)}";
        ContinueButton.Content = PlaybackIsPlaying ? "Pause" : "Resume";
        if (_continueArtworkUrl == track.ArtworkUrl) return;
        _continueArtworkUrl = track.ArtworkUrl;
        SetImage(ContinueArtwork, track.ArtworkUrl);
    }

    private async Task LoadHomeAsync(bool refresh = false, string? mood = null)
    {
        _homeCancel?.Cancel();
        _homeCancel?.Dispose();
        var cancellation = new CancellationTokenSource();
        _homeCancel = cancellation;
        _homeMood = mood;
        RefreshHomeButton.IsEnabled = false;
        HomeError.Visibility = Visibility.Collapsed;
        if (HomeSections.ItemsSource is null) HomeLoading.Visibility = Visibility.Visible;
        CollectionPanel.Visibility = Visibility.Collapsed;
        HomeScroll.Visibility = Visibility.Visible;
        if (mood is null)
        {
            HomeBackButton.Visibility = Visibility.Collapsed;
            HomeGreeting.Text = GreetingForNow();
            HomeTagline.Text = "Fresh music, familiar favorites, and something for right now.";
            ContinueCard.Visibility = _player.CurrentTrack is null ? Visibility.Collapsed : Visibility.Visible;
        }
        else
        {
            HomeBackButton.Visibility = Visibility.Visible;
            HomeGreeting.Text = mood;
            HomeTagline.Text = $"Music for when you want to {mood.ToLowerInvariant()}.";
            ContinueCard.Visibility = Visibility.Collapsed;
            MoodArea.Visibility = Visibility.Collapsed;
        }
        try
        {
            var seed = _player.CurrentTrack?.Id ?? _library.RecentlyPlayed.FirstOrDefault()?.Track.Id ?? _library.Favorites.FirstOrDefault()?.Track.Id;
            var discovery = mood is null
                ? await _discoveryProvider.HomeAsync(seed, refresh, cancellation.Token)
                : await _discoveryProvider.MoodAsync(mood, refresh, cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            var sections = new List<DiscoverySection>();
            if (mood is null && _library.RecentlyPlayed.Count > 0)
            {
                sections.Add(new DiscoverySection
                {
                    Title = "Recently played",
                    Items = _library.RecentlyPlayed.Take(12).Select(entry => new DiscoveryItem
                    {
                        Id = entry.Track.Id, Kind = "song", Title = entry.Track.Title, Subtitle = entry.Track.Artist,
                        ArtworkUrl = entry.Track.ArtworkUrl, Track = entry.Track, CanRemoveFromHistory = true
                    }).ToList()
                });
            }
            sections.AddRange(discovery.Sections.Where(section => section.Items.Count > 0));
            HomeSections.ItemsSource = sections;
            RefreshFavoriteStates();
            MoodChips.ItemsSource = discovery.Moods;
            MoodArea.Visibility = mood is null && discovery.Moods.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            HomeError.Visibility = sections.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            HomeErrorText.Text = sections.Count == 0 ? "Nothing new appeared yet" : "";
            if (mood is null) _homeLoaded = true;
            if (discovery.Stale) SetStatus("Showing saved Home recommendations");
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!cancellation.IsCancellationRequested)
            {
                HomeErrorText.Text = "Home couldn’t refresh";
                HomeError.Visibility = Visibility.Visible;
                SetStatus(error.Message, true);
            }
        }
        finally
        {
            if (ReferenceEquals(_homeCancel, cancellation))
            {
                HomeLoading.Visibility = Visibility.Collapsed;
                RefreshHomeButton.IsEnabled = true;
            }
        }
    }

    private static string GreetingForNow() => DateTime.Now.Hour switch
    {
        < 12 => "Good morning",
        < 18 => "Good afternoon",
        _ => "Good evening"
    };

    private async void RefreshHome_Click(object sender, RoutedEventArgs e) => await LoadHomeAsync(true, _homeMood);
    private async void HomeBack_Click(object sender, RoutedEventArgs e) => await LoadHomeAsync(false);
    private void Continue_Click(object sender, RoutedEventArgs e) => TogglePlayback();
    private async void Mood_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is string mood) await LoadHomeAsync(false, mood);
    }

    private void HomeShelf_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scroll) return;
        scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta * .7);
        e.Handled = true;
    }

    private async void DiscoveryCard_Click(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source is not null && !ReferenceEquals(source, sender))
        {
            if (source is System.Windows.Controls.Primitives.ButtonBase) return;
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }
        if ((sender as FrameworkElement)?.DataContext is DiscoveryItem item) await ActivateDiscoveryAsync(item);
    }

    private async void DiscoveryPrimary_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is DiscoveryItem item) await ActivateDiscoveryAsync(item);
    }

    private async Task ActivateDiscoveryAsync(DiscoveryItem item)
    {
        if (item.Kind == "song" && item.Track is not null) await PlaySearchResultAsync(item.Track);
        else if (item.Kind is "album" or "playlist") await OpenCollectionAsync(item);
    }

    private async void AddDiscovery_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not DiscoveryItem { Track: { } track }) return;
        _library.Queue.Add(track);
        MarkQueueEdited();
        await SaveAsync();
        SetStatus($"Added {track.Title} to queue");
    }

    private async void RemoveRecent_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not DiscoveryItem item) return;
        var history = _library.RecentlyPlayed.FirstOrDefault(entry => entry.Track.Id == item.Id);
        if (history is null) return;
        _library.RecentlyPlayed.Remove(history);
        await SaveAsync();
        if (HomeSections.ItemsSource is IEnumerable<DiscoverySection> current)
        {
            var sections = current.ToList();
            var recent = sections.FirstOrDefault(section => section.Title == "Recently played");
            recent?.Items.RemoveAll(card => card.Id == item.Id);
            HomeSections.ItemsSource = sections.Where(section => section.Items.Count > 0).ToList();
        }
        SetStatus($"Removed {item.Title} from recently played");
    }

    private async Task OpenCollectionAsync(DiscoveryItem item)
    {
        HomeScroll.Visibility = Visibility.Collapsed;
        CollectionPanel.Visibility = Visibility.Visible;
        CollectionLoading.Visibility = Visibility.Visible;
        CollectionEmpty.Visibility = Visibility.Collapsed;
        CollectionTrackList.Visibility = Visibility.Collapsed;
        PlayCollectionButton.IsEnabled = false;
        AddCollectionButton.IsEnabled = false;
        CollectionTitle.Text = item.Title;
        CollectionSubtitle.Text = item.Subtitle;
        CollectionCount.Text = "Loading…";
        SetImage(CollectionArtwork, item.ArtworkUrl);
        try
        {
            var detail = await _discoveryProvider.CollectionAsync(item.Kind, item.Id);
            _collectionDetail = detail;
            for (var index = 0; index < detail.Tracks.Count; index++)
            {
                detail.Tracks[index].Number = (index + 1).ToString("00");
                detail.Tracks[index].Refresh();
            }
            CollectionTitle.Text = detail.Title;
            CollectionSubtitle.Text = string.IsNullOrWhiteSpace(detail.Subtitle) ? item.Subtitle : detail.Subtitle;
            CollectionCount.Text = detail.CountText;
            SetImage(CollectionArtwork, detail.ArtworkUrl ?? item.ArtworkUrl);
            CollectionTrackList.ItemsSource = detail.Tracks;
            RefreshFavoriteStates();
            CollectionTrackList.Visibility = detail.Tracks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            CollectionEmpty.Visibility = detail.Tracks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            CollectionEmptyText.Text = detail.Tracks.Count == 0 ? "No playable songs found" : "";
            PlayCollectionButton.IsEnabled = detail.Tracks.Count > 0;
            AddCollectionButton.IsEnabled = detail.Tracks.Count > 0;
        }
        catch (Exception error)
        {
            _collectionDetail = null;
            CollectionEmptyText.Text = "Couldn’t open this collection";
            CollectionEmpty.Visibility = Visibility.Visible;
            SetStatus(error.Message, true);
        }
        finally { CollectionLoading.Visibility = Visibility.Collapsed; }
    }

    private void CollectionBack_Click(object sender, RoutedEventArgs e)
    {
        CollectionPanel.Visibility = Visibility.Collapsed;
        HomeScroll.Visibility = Visibility.Visible;
        _collectionDetail = null;
    }

    private async void PlayCollection_Click(object sender, RoutedEventArgs e)
    {
        if (_collectionDetail is not { Tracks.Count: > 0 } detail) return;
        _library.Queue.Clear();
        foreach (var track in detail.Tracks) _library.Queue.Add(track);
        _library.QueueSourceId = $"{detail.Kind}:{detail.Id}";
        _library.QueueSourceName = detail.Title;
        _playbackDirty = true;
        await SaveAsync();
        await _player.PlayAsync(0);
    }

    private async void AddCollection_Click(object sender, RoutedEventArgs e)
    {
        if (_collectionDetail is not { Tracks.Count: > 0 } detail) return;
        foreach (var track in detail.Tracks) _library.Queue.Add(track);
        MarkQueueEdited();
        await SaveAsync();
        SetStatus($"Added {detail.Tracks.Count} songs to queue");
    }

    private async void CollectionTrack_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(CollectionTrackList, source) is not ListBoxItem item || item.DataContext is not Track track) return;
        await PlaySearchResultAsync(track);
        e.Handled = true;
    }

    private async void AddCollectionTrack_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not Track track) return;
        _library.Queue.Add(track);
        MarkQueueEdited();
        await SaveAsync();
        SetStatus($"Added {track.Title} to queue");
    }

    private static void SetImage(System.Windows.Controls.Image image, string? url)
    {
        try { image.Source = string.IsNullOrWhiteSpace(url) ? null : new BitmapImage(new Uri(url)); }
        catch { image.Source = null; }
    }

    private static string FormatTime(TimeSpan value) => value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");
    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
    private void SetStatus(string text, bool error = false)
    {
        StatusText.Text = Truncate(text.ReplaceLineEndings(" "), 90);
        StatusText.Foreground = error ? System.Windows.Media.Brushes.LightCoral : System.Windows.Media.Brushes.White;
        StatusPill.Background = new System.Windows.Media.SolidColorBrush(error
            ? System.Windows.Media.Color.FromRgb(75, 35, 42)
            : System.Windows.Media.Color.FromRgb(37, 36, 44));
        StatusPill.Visibility = Visibility.Visible;
        _statusTimer.Stop(); _statusTimer.Start();
    }

    private void HomeNav_Click(object sender, RoutedEventArgs e) => SetMode("home");
    private void SearchNav_Click(object sender, RoutedEventArgs e) => SetMode("search");
    private void AiNav_Click(object sender, RoutedEventArgs e) => SetMode("ai");
    private void FavoritesNav_Click(object sender, RoutedEventArgs e) { LibraryTabs.SelectedIndex = 0; LibraryHeading.Text = "Liked songs"; LibraryDescription.Text = "Songs you have saved."; SetMode("library"); }
    private void SavedNav_Click(object sender, RoutedEventArgs e) { LibraryTabs.SelectedIndex = 1; LibraryHeading.Text = "Saved queues"; LibraryDescription.Text = "Queues you can return to whenever you want."; SetMode("library"); }
    private void EmptySearch_Click(object sender, RoutedEventArgs e) => SetMode("search");
    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync();
    private async void SearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await SearchAsync(); } }
    private async void AiSend_Click(object sender, RoutedEventArgs e) => await SendToAiAsync();
    private async void AiBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await SendToAiAsync(); } }

    private async void AddResult_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Track track) return;
        _library.Queue.Add(track); MarkQueueEdited(); await SaveAsync(); SetStatus($"Added {track.Title} to queue");
    }

    private async void PlayResult_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Track track) await PlaySearchResultAsync(track);
    }

    private async Task PlaySearchResultAsync(Track track)
    {
        var index = _library.Queue.ToList().FindIndex(item => item.Id == track.Id);
        if (index < 0) { _library.Queue.Add(track); MarkQueueEdited(); await SaveAsync(); index = _library.Queue.Count - 1; }
        await _player.PlayAsync(index);
    }

    private async void SearchResults_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SearchResults.SelectedItem is not Track track) return;
        await PlaySearchResultAsync(track);
    }

    private async void QueueList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (QueueList.SelectedIndex >= 0) await _player.PlayAsync(QueueList.SelectedIndex);
    }

    private async void RemoveQueue_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Track track) return;
        _library.Queue.Remove(track); MarkQueueEdited(); await SaveAsync(); SetStatus($"Removed {track.Title}");
    }

    private async void FavoriteTrack_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var track = (sender as FrameworkElement)?.DataContext switch
        {
            Track direct => direct,
            DiscoveryItem { Track: { } discovered } => discovered,
            _ => null
        };
        if (track is null) return;
        var existing = _library.Favorites.FirstOrDefault(x => x.Track.Id == track.Id);
        if (existing is null)
        {
            _library.Favorites.Add(new() { Track = track });
            await SaveAsync();
            SetStatus($"Liked {track.Title}");
        }
        else
        {
            _library.Favorites.Remove(existing);
            await SaveAsync();
            SetStatus($"Removed {track.Title} from liked songs");
        }
    }

    private void RefreshFavoriteStates()
    {
        var favoriteIds = _library.Favorites.Select(item => item.Track.Id).ToHashSet(StringComparer.Ordinal);
        var tracks = new HashSet<Track>();
        foreach (var track in _library.Queue) tracks.Add(track);
        foreach (var favorite in _library.Favorites) tracks.Add(favorite.Track);
        foreach (var saved in _library.SavedQueues)
        foreach (var track in saved.Tracks) tracks.Add(track);
        foreach (var history in _library.RecentlyPlayed) tracks.Add(history.Track);
        if (SearchResults.ItemsSource is IEnumerable<Track> results)
            foreach (var track in results) tracks.Add(track);
        if (HomeSections.ItemsSource is IEnumerable<DiscoverySection> sections)
            foreach (var item in sections.SelectMany(section => section.Items))
                if (item.Track is { } track) tracks.Add(track);
        if (_collectionDetail is { } collection)
            foreach (var track in collection.Tracks) tracks.Add(track);
        if (_player.CurrentTrack is { } current) tracks.Add(current);
        foreach (var track in tracks) track.IsFavorite = favoriteIds.Contains(track.Id);
    }

    private async void ClearQueue_Click(object sender, RoutedEventArgs e)
    {
        _library.Queue.Clear(); MarkQueueEdited(); await SaveAsync(); SetStatus("Queue cleared");
    }

    private async void SaveQueue_Click(object sender, RoutedEventArgs e)
    {
        var path = PromptDialog.Show(this, "Save queue", "Name or folder/name", "My queue");
        if (string.IsNullOrWhiteSpace(path)) return;
        var parts = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return;
        var folder = EnsureFolder(string.Join('/', parts.SkipLast(1)));
        var name = parts[^1];
        var existing = _library.SavedQueues.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && x.FolderId == folder?.Id);
        if (existing is null) _library.SavedQueues.Add(new() { Name = name, FolderId = folder?.Id, Tracks = _library.Queue.ToList() });
        else { existing.Tracks = _library.Queue.ToList(); existing.UpdatedAt = DateTimeOffset.Now; }
        await SaveAsync(); SetStatus($"Saved queue “{name}”");
    }

    private LibraryFolder? EnsureFolder(string path)
    {
        var parts = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? parent = null; LibraryFolder? folder = null;
        foreach (var name in parts)
        {
            folder = _library.Folders.FirstOrDefault(x => x.ParentId == parent && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (folder is null) { folder = new() { Name = name, ParentId = parent }; _library.Folders.Add(folder); }
            parent = folder.Id;
        }
        return folder;
    }

    private async void LoadSaved_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SavedQueue saved) return;
        _library.Queue.Clear(); foreach (var track in saved.Tracks) _library.Queue.Add(track);
        _library.QueueSourceId = saved.Id;
        _library.QueueSourceName = string.IsNullOrWhiteSpace(saved.DisplayPath) ? saved.Name : saved.DisplayPath;
        _playbackDirty = true;
        await SaveAsync(); SetStatus($"Loaded “{saved.Name}”");
    }

    private async void SavedTrack_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Track track ||
            (sender as FrameworkElement)?.Tag is not SavedQueue saved) return;
        _library.Queue.Clear();
        foreach (var item in saved.Tracks) _library.Queue.Add(item);
        _library.QueueSourceId = saved.Id;
        _library.QueueSourceName = string.IsNullOrWhiteSpace(saved.DisplayPath) ? saved.Name : saved.DisplayPath;
        _playbackDirty = true;
        await SaveAsync();
        var index = _library.Queue.ToList().FindIndex(item => item.Id == track.Id);
        if (index >= 0) await _player.PlayAsync(index);
        e.Handled = true;
    }

    private async void DeleteSaved_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SavedQueue saved) return;
        if (_library.QueueSourceId == saved.Id) MarkQueueEdited();
        _library.SavedQueues.Remove(saved); await SaveAsync();
    }

    private async void AddFavorite_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Favorite favorite) return;
        _library.Queue.Add(favorite.Track); MarkQueueEdited(); await SaveAsync(); SetStatus($"Added {favorite.Track.Title} to queue");
    }

    private async void Unfavorite_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Favorite favorite) return;
        _library.Favorites.Remove(favorite); await SaveAsync();
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlayback();
    private async void Next_Click(object sender, RoutedEventArgs e) => await NextPlaybackAsync();
    private async void Previous_Click(object sender, RoutedEventArgs e) => await PreviousPlaybackAsync();
    private async void Shuffle_Click(object sender, RoutedEventArgs e)
    {
        _library.ShuffleEnabled = !_library.ShuffleEnabled;
        _player.ShuffleEnabled = _library.ShuffleEnabled;
        UpdatePlaybackModeButtons();
        await SaveAsync();
    }
    private async void Repeat_Click(object sender, RoutedEventArgs e)
    {
        _player.RepeatMode = _player.RepeatMode switch
        {
            PlaybackRepeatMode.Off => PlaybackRepeatMode.All,
            PlaybackRepeatMode.All => PlaybackRepeatMode.One,
            _ => PlaybackRepeatMode.Off
        };
        _library.RepeatMode = _player.RepeatMode.ToString().ToLowerInvariant();
        UpdatePlaybackModeButtons();
        await SaveAsync();
    }
    private void SeekSlider_MouseUp(object sender, MouseButtonEventArgs e) => SeekPlayback(SeekSlider.Value);
    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!AppRuntime.IsTestMode || _updatingSeekSlider || !_playbackRestoreComplete) return;
        AppRuntime.TestLog($"automation seek={e.NewValue:F2}");
        SeekPlayback(e.NewValue);
    }
    private void NowPlayingSection_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _pointerOverNowPlaying = true;
        _miniQueueCloseTimer.Stop();
        if (!_mini || _library.Queue.Count == 0) return;
        MiniQueueList.SelectedIndex = _player.CurrentIndex;
        MiniQueuePopup.IsOpen = true;
        if (_player.CurrentIndex >= 0 && _player.CurrentIndex < _library.Queue.Count)
            Dispatcher.BeginInvoke(() => MiniQueueList.ScrollIntoView(_library.Queue[_player.CurrentIndex]), DispatcherPriority.Loaded);
    }
    private void NowPlayingSection_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _pointerOverNowPlaying = false;
        ScheduleMiniQueueClose();
    }
    private void MiniQueueSurface_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _pointerOverMiniQueue = true;
        _miniQueueCloseTimer.Stop();
    }
    private void MiniQueueSurface_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _pointerOverMiniQueue = false;
        ScheduleMiniQueueClose();
    }
    private async void MiniQueueList_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(MiniQueueList, source) is not ListBoxItem item || item.DataContext is not Track track) return;
        var index = _library.Queue.IndexOf(track);
        if (index < 0) return;
        CloseMiniQueue();
        await _player.PlayAsync(index);
        e.Handled = true;
    }
    private void ScheduleMiniQueueClose()
    {
        _miniQueueCloseTimer.Stop();
        _miniQueueCloseTimer.Start();
    }
    private void CloseMiniQueue()
    {
        _miniQueueCloseTimer.Stop();
        MiniQueuePopup.IsOpen = false;
        _pointerOverNowPlaying = false;
        _pointerOverMiniQueue = false;
    }
    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_player is not null) _player.Volume = e.NewValue;
        if (_videoActive && _videoUsesOwnAudio) VideoPlayer.Volume = AppRuntime.AudibleVolume(e.NewValue) / 100;
        if (!_settingsLoaded) return;
        _library.Volume = e.NewValue;
        _volumeSaveTimer.Stop();
        _volumeSaveTimer.Start();
    }
    private void Topmost_Click(object sender, RoutedEventArgs e)
    {
        _pinned = !_pinned;
        Topmost = _mini || _pinned;
        TopmostButton.Foreground = _pinned ? (System.Windows.Media.Brush)FindResource("Accent") : System.Windows.Media.Brushes.White;
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) { if (_videoActive) CloseVideo(); WindowState = WindowState.Minimized; }
    private void Hide_Click(object sender, RoutedEventArgs e) { if (_videoActive) CloseVideo(); Hide(); }
    private void OpenData_Click(object sender, RoutedEventArgs e) { Directory.CreateDirectory(_store.DataDirectory); Process.Start(new ProcessStartInfo("explorer.exe", _store.DataDirectory) { UseShellExecute = true }); }
    private void Mini_Click(object sender, RoutedEventArgs e) => ToggleMini();

    private void ToggleMini()
    {
        if (_videoActive) CloseVideo();
        CloseMiniQueue();
        _mini = !_mini;
        if (_mini)
        {
            _fullBounds = new Rect(Left, Top, Width, Height);
            BodyRow.Height = new GridLength(0); TitleRow.Height = new GridLength(0); Body.Visibility = Visibility.Collapsed; TitleBar.Visibility = Visibility.Collapsed;
            MinWidth = 320; MinHeight = 98; MaxHeight = 98; Width = 560; Height = 98; Topmost = true;
        }
        else
        {
            MaxHeight = double.PositiveInfinity; MinWidth = 1040; MinHeight = 620;
            Width = Math.Max(1040, _fullBounds.Width); Height = Math.Max(620, _fullBounds.Height); Left = _fullBounds.Left; Top = _fullBounds.Top;
            Body.Visibility = Visibility.Visible; BodyRow.Height = new GridLength(1, GridUnitType.Star); TitleRow.Height = new GridLength(58); TitleBar.Visibility = Visibility.Visible;
            Topmost = _pinned;
        }
        ApplyPlayerBarLayout();
    }

    private void ApplyPlayerBarLayout()
    {
        if (PlayerBar is null) return;
        if (!_mini)
        {
            NowPlayingColumn.Width = new GridLength(2.2, GridUnitType.Star);
            TransportColumn.Width = new GridLength(3, GridUnitType.Star);
            UtilityColumn.Width = new GridLength(2.2, GridUnitType.Star);
            NowPlayingSection.Margin = new Thickness(18, 10, 18, 10);
            ArtworkColumn.Width = new GridLength(62);
            ArtworkFrame.Visibility = Visibility.Visible;
            ArtworkFrame.Width = ArtworkFrame.Height = 58;
            NowTextPanel.Margin = new Thickness(12, 0, 8, 0);
            NowTitle.FontSize = 13;
            NowArtist.Visibility = Visibility.Visible;
            TransportSection.Margin = new Thickness(8, 0, 8, 0);
            ShuffleButton.Visibility = Visibility.Visible;
            RepeatButton.Visibility = Visibility.Visible;
            PositionColumn.Width = new GridLength(38);
            DurationColumn.Width = new GridLength(38);
            PositionText.Visibility = Visibility.Visible;
            DurationText.Visibility = Visibility.Visible;
            UtilitySection.Margin = new Thickness(0, 0, 18, 0);
            VolumeGlyph.Visibility = Visibility.Visible;
            VolumeSlider.Visibility = Visibility.Visible;
            WatchVideoButton.Visibility = Visibility.Visible;
            UtilityBottomRow.Height = new GridLength(0);
            UtilityVolumeGlyphColumn.Width = GridLength.Auto;
            UtilityVolumeColumn.Width = GridLength.Auto;
            UtilityVideoColumn.Width = GridLength.Auto;
            UtilityExpandColumn.Width = GridLength.Auto;
            Grid.SetRow(VolumeSlider, 0); Grid.SetRowSpan(VolumeSlider, 1); Grid.SetColumn(VolumeSlider, 1);
            Grid.SetRow(WatchVideoButton, 0); Grid.SetColumn(WatchVideoButton, 2);
            Grid.SetRow(ExpandButton, 0); Grid.SetColumn(ExpandButton, 3);
            VolumeSlider.Style = (Style)FindResource("ThemedSlider");
            VolumeSlider.Orientation = System.Windows.Controls.Orientation.Horizontal;
            VolumeSlider.Width = 86; VolumeSlider.Height = 18; VolumeSlider.Margin = new Thickness(0);
            WatchVideoButton.Margin = new Thickness(8, 0, 0, 0);
            ExpandButton.Margin = new Thickness(2, 0, 0, 0);
            MiniQueueSurface.Width = 380;
            return;
        }

        var width = ActualWidth > 0 ? ActualWidth : Width;
        var albumWidth = width >= 680 ? 190 : width >= 520 ? 170 : width >= 380 ? 136 : 90;
        var showArtwork = width >= 380;
        var showTimes = width >= 520;

        NowPlayingColumn.Width = new GridLength(albumWidth);
        TransportColumn.Width = new GridLength(1, GridUnitType.Star);
        UtilityColumn.Width = new GridLength(54);
        NowPlayingSection.Margin = new Thickness(6, 10, 3, 10);

        ArtworkFrame.Visibility = showArtwork ? Visibility.Visible : Visibility.Collapsed;
        ArtworkColumn.Width = new GridLength(showArtwork ? width >= 520 ? 48 : 43 : 0);
        ArtworkFrame.Width = ArtworkFrame.Height = width >= 520 ? 44 : 39;
        NowTextPanel.Margin = new Thickness(showArtwork ? 7 : 3, 0, 3, 0);
        NowTitle.FontSize = width >= 520 ? 12 : 11;
        NowArtist.Visibility = Visibility.Visible;

        TransportSection.Margin = new Thickness(0);
        ShuffleButton.Visibility = Visibility.Visible;
        RepeatButton.Visibility = Visibility.Visible;
        PositionColumn.Width = new GridLength(showTimes ? 38 : 0);
        DurationColumn.Width = new GridLength(showTimes ? 38 : 0);
        PositionText.Visibility = showTimes ? Visibility.Visible : Visibility.Collapsed;
        DurationText.Visibility = showTimes ? Visibility.Visible : Visibility.Collapsed;

        UtilitySection.Margin = new Thickness(0, 3, 2, 3);
        UtilityBottomRow.Height = new GridLength(1, GridUnitType.Star);
        UtilityVolumeGlyphColumn.Width = new GridLength(18);
        UtilityVolumeColumn.Width = new GridLength(34);
        UtilityVideoColumn.Width = new GridLength(0);
        UtilityExpandColumn.Width = new GridLength(0);
        VolumeGlyph.Visibility = Visibility.Collapsed;
        VolumeSlider.Visibility = Visibility.Visible;
        WatchVideoButton.Visibility = Visibility.Visible;
        Grid.SetRow(VolumeSlider, 0); Grid.SetRowSpan(VolumeSlider, 2); Grid.SetColumn(VolumeSlider, 0);
        Grid.SetRow(WatchVideoButton, 0); Grid.SetColumn(WatchVideoButton, 1);
        Grid.SetRow(ExpandButton, 1); Grid.SetColumn(ExpandButton, 1);
        VolumeSlider.Style = (Style)FindResource("VerticalThemedSlider");
        VolumeSlider.Orientation = System.Windows.Controls.Orientation.Vertical;
        VolumeSlider.Width = 18; VolumeSlider.Height = 62; VolumeSlider.Margin = new Thickness(0);
        WatchVideoButton.Margin = new Thickness(1, 0, 0, 0);
        ExpandButton.Margin = new Thickness(1, 0, 0, 0);
        MiniQueueSurface.Width = Math.Clamp(width - 16, 288, 380);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; else DragMove(); }
    private void PlayerBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_mini) return;
        var element = e.OriginalSource as DependencyObject;
        while (element is not null)
        {
            if (element is System.Windows.Controls.Primitives.ButtonBase or Slider) return;
            element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        }
        DragMove();
    }
    private void Window_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (e.Text == ":" && Keyboard.FocusedElement is not System.Windows.Controls.TextBox) { SetMode("ai"); e.Handled = true; }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_reallyClose) return;
        e.Cancel = true;
        ExitApp();
    }

    private static void TrimWorkingSet()
    {
        try { SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, new IntPtr(-1), new IntPtr(-1)); } catch { }
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimumWorkingSetSize, IntPtr maximumWorkingSetSize);

    private async void ExitApp()
    {
        if (_reallyClose) return;
        _reallyClose = true;
        _volumeSaveTimer.Stop();
        _playbackSaveTimer.Stop();
        _fileDebounce.Stop();
        _miniQueueCloseTimer.Stop();
        _statusTimer.Stop();
        _updateTimer.Stop();
        _watcher?.Dispose();
        _watcher = null;
        _agent.Cancel();
        _lyricsCancel?.Cancel();
        _homeCancel?.Cancel();
        _videoSearchCancel?.Cancel();
        if (_settingsLoaded)
        {
            CapturePlaybackState();
            try { await _store.SaveAsync(_library); } catch { }
        }
        if (_tray is not null) _tray.Visible = false;
        _tray?.Dispose();
        _tray = null;
        _lyrics.Dispose();
        _videoCancel?.Cancel();
        try { VideoPlayer.Stop(); VideoPlayer.Source = null; } catch { }
        _discoveryProvider.Dispose();
        _updates.Dispose();
        _player.Dispose();
        System.Windows.Application.Current.Shutdown();
    }
}
