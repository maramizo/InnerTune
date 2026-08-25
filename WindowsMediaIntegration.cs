using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Windows.Media;
using Windows.Storage.Streams;

namespace InnerTune;

public readonly record struct WindowsMediaState(
    Track? Track,
    bool IsPlaying,
    bool IsLoading,
    TimeSpan Position,
    TimeSpan Duration,
    bool HasPrevious,
    bool HasNext);

public sealed class WindowsMediaIntegration : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Action _toggle;
    private readonly Action _previous;
    private readonly Action _next;
    private readonly Action<double> _seek;
    private readonly IntPtr _windowHandle;
    private readonly TaskbarItemInfo _taskbar = new();
    private readonly ThumbButtonInfo _previousButton;
    private readonly ThumbButtonInfo _playButton;
    private readonly ThumbButtonInfo _nextButton;
    private SystemMediaTransportControls? _transport;
    private bool? _lastPlaying;
    private bool? _lastThumbnailPreviousEnabled;
    private bool? _lastThumbnailNextEnabled;
    private TaskbarItemProgressState _lastProgressState = unchecked((TaskbarItemProgressState)(-1));
    private long _lastProgress = -1;
    private long _lastDuration = -1;
    private string? _lastTrackId;
    private long _lastTimelineSecond = -1;
    private bool? _lastTransportEnabled;
    private bool? _lastPreviousEnabled;
    private bool? _lastNextEnabled;
    private MediaPlaybackStatus? _lastMediaStatus;
    private bool _disposed;
    private readonly ImageSource _playIcon = CreateTaskbarImage(TaskbarIcon.Play);
    private readonly ImageSource _pauseIcon = CreateTaskbarImage(TaskbarIcon.Pause);

    public WindowsMediaIntegration(Window window, Action toggle, Action previous, Action next, Action<double> seek)
    {
        _dispatcher = window.Dispatcher;
        _toggle = toggle;
        _previous = previous;
        _next = next;
        _seek = seek;
        _windowHandle = new WindowInteropHelper(window).Handle;
        _previousButton = CreateThumbnailButton(CreateTaskbarImage(TaskbarIcon.Previous), "Previous");
        _playButton = CreateThumbnailButton(_playIcon, "Play");
        _nextButton = CreateThumbnailButton(CreateTaskbarImage(TaskbarIcon.Next), "Next");
        _previousButton.Click += (_, _) => _dispatcher.BeginInvoke(_previous);
        _playButton.Click += (_, _) => _dispatcher.BeginInvoke(_toggle);
        _nextButton.Click += (_, _) => _dispatcher.BeginInvoke(_next);
        _taskbar.ThumbButtonInfos.Add(_previousButton);
        _taskbar.ThumbButtonInfos.Add(_playButton);
        _taskbar.ThumbButtonInfos.Add(_nextButton);
        window.TaskbarItemInfo = _taskbar;
        AppRuntime.TestLog("taskbar thumbnail controls configured=3");
        InitializeSystemMediaControls();
    }

    public void Update(WindowsMediaState state)
    {
        if (_disposed) return;
        UpdateThumbnailButton(state);
        UpdateTaskbarProgress(state);
        UpdateSystemMediaControls(state);
    }

    private void UpdateThumbnailButton(WindowsMediaState state)
    {
        if (_lastPlaying != state.IsPlaying)
        {
            _playButton.ImageSource = state.IsPlaying ? _pauseIcon : _playIcon;
            _playButton.Description = state.IsPlaying ? "Pause" : "Play";
            _lastPlaying = state.IsPlaying;
        }
        if (_lastThumbnailPreviousEnabled != state.HasPrevious)
        {
            _previousButton.IsEnabled = state.HasPrevious;
            _lastThumbnailPreviousEnabled = state.HasPrevious;
        }
        if (_lastThumbnailNextEnabled != state.HasNext)
        {
            _nextButton.IsEnabled = state.HasNext;
            _lastThumbnailNextEnabled = state.HasNext;
        }
    }

    private void UpdateTaskbarProgress(WindowsMediaState state)
    {
        var progressState = state.Track is null || state.Duration <= TimeSpan.Zero
            ? TaskbarItemProgressState.None
            : state.IsLoading ? TaskbarItemProgressState.Indeterminate
            : state.IsPlaying ? TaskbarItemProgressState.Normal : TaskbarItemProgressState.Paused;
        var progress = Math.Max(0, (long)state.Position.TotalMilliseconds);
        var duration = Math.Max(1, (long)state.Duration.TotalMilliseconds);
        if (progressState == _lastProgressState &&
            (progressState is TaskbarItemProgressState.None or TaskbarItemProgressState.Indeterminate ||
             progress / 500 == _lastProgress / 500 && duration == _lastDuration)) return;
        if (progressState is TaskbarItemProgressState.Normal or TaskbarItemProgressState.Paused or TaskbarItemProgressState.Error)
            _taskbar.ProgressValue = Math.Clamp((double)progress / duration, 0, 1);
        _taskbar.ProgressState = progressState;
        _lastProgressState = progressState;
        _lastProgress = progress;
        _lastDuration = duration;
    }

    private void InitializeSystemMediaControls()
    {
        try
        {
            _transport = SystemMediaTransportControlsInterop.GetForWindow(_windowHandle);
            _transport.IsEnabled = false;
            _transport.IsPlayEnabled = true;
            _transport.IsPauseEnabled = true;
            _transport.IsNextEnabled = true;
            _transport.IsPreviousEnabled = true;
            _transport.ButtonPressed += TransportButtonPressed;
            _transport.PlaybackPositionChangeRequested += PlaybackPositionChangeRequested;
        }
        catch
        {
            _transport = null;
        }
    }

    private void UpdateSystemMediaControls(WindowsMediaState state)
    {
        if (_transport is null) return;
        try
        {
            var enabled = state.Track is not null;
            if (_lastTransportEnabled != enabled) { _transport.IsEnabled = enabled; _lastTransportEnabled = enabled; }
            if (_lastPreviousEnabled != state.HasPrevious) { _transport.IsPreviousEnabled = state.HasPrevious; _lastPreviousEnabled = state.HasPrevious; }
            if (_lastNextEnabled != state.HasNext) { _transport.IsNextEnabled = state.HasNext; _lastNextEnabled = state.HasNext; }
            var status = state.Track is null ? MediaPlaybackStatus.Closed
                : state.IsLoading ? MediaPlaybackStatus.Changing
                : state.IsPlaying ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
            if (_lastMediaStatus != status) { _transport.PlaybackStatus = status; _lastMediaStatus = status; }

            if (_lastTrackId != state.Track?.Id)
            {
                _lastTrackId = state.Track?.Id;
                _lastTimelineSecond = -1;
                var updater = _transport.DisplayUpdater;
                updater.ClearAll();
                if (state.Track is not null)
                {
                    updater.Type = MediaPlaybackType.Music;
                    updater.MusicProperties.Title = state.Track.Title;
                    updater.MusicProperties.Artist = state.Track.Artist;
                    updater.MusicProperties.AlbumTitle = state.Track.Album ?? "";
                    try
                    {
                        if (Uri.TryCreate(state.Track.ArtworkUrl, UriKind.Absolute, out var artwork))
                            updater.Thumbnail = RandomAccessStreamReference.CreateFromUri(artwork);
                    }
                    catch { }
                }
                updater.Update();
            }

            var timelineSecond = (long)state.Position.TotalSeconds;
            if (state.Track is not null && state.Duration > TimeSpan.Zero && timelineSecond != _lastTimelineSecond)
            {
                var duration = state.Duration;
                _transport.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
                {
                    StartTime = TimeSpan.Zero,
                    MinSeekTime = TimeSpan.Zero,
                    Position = state.Position < duration ? state.Position : duration,
                    MaxSeekTime = duration,
                    EndTime = duration
                });
                _lastTimelineSecond = timelineSecond;
            }
        }
        catch { }
    }

    private void TransportButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        _dispatcher.BeginInvoke(() =>
        {
            switch (args.Button)
            {
                case SystemMediaTransportControlsButton.Play:
                case SystemMediaTransportControlsButton.Pause: _toggle(); break;
                case SystemMediaTransportControlsButton.Previous: _previous(); break;
                case SystemMediaTransportControlsButton.Next: _next(); break;
            }
        });
    }

    private void PlaybackPositionChangeRequested(SystemMediaTransportControls sender, PlaybackPositionChangeRequestedEventArgs args) =>
        _dispatcher.BeginInvoke(() => _seek(args.RequestedPlaybackPosition.TotalSeconds));

    private static ThumbButtonInfo CreateThumbnailButton(ImageSource icon, string tooltip) => new()
    {
        ImageSource = icon,
        Description = tooltip,
        IsEnabled = true,
        IsBackgroundVisible = true,
        DismissWhenClicked = false,
        Visibility = Visibility.Visible
    };

    private static ImageSource CreateTaskbarImage(TaskbarIcon icon)
    {
        var geometry = icon switch
        {
            TaskbarIcon.Previous => "M5,3 V17 M17,3 L8,10 17,17",
            TaskbarIcon.Play => "M6,3 L17,10 6,17 Z",
            TaskbarIcon.Pause => "M6,3 V17 M14,3 V17",
            _ => "M3,3 L12,10 3,17 M15,3 V17"
        };
        var drawing = new GeometryDrawing
        {
            Geometry = Geometry.Parse(geometry),
            Brush = icon == TaskbarIcon.Play ? System.Windows.Media.Brushes.White : null,
            Pen = icon == TaskbarIcon.Play ? null : new System.Windows.Media.Pen(System.Windows.Media.Brushes.White, 1.55)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            }
        };
        drawing.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_transport is not null)
        {
            _transport.ButtonPressed -= TransportButtonPressed;
            _transport.PlaybackPositionChangeRequested -= PlaybackPositionChangeRequested;
            try { _transport.IsEnabled = false; } catch { }
            _transport = null;
        }
        _taskbar.ProgressState = TaskbarItemProgressState.None;
        _taskbar.ThumbButtonInfos.Clear();
    }

    private enum TaskbarIcon { Previous, Play, Pause, Next }

}
