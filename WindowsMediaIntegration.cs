using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
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
    private const int WmCommand = 0x0111;
    private const int ThumbnailButtonClicked = 0x1800;
    private const uint PreviousButtonId = 0x4901;
    private const uint PlayButtonId = 0x4902;
    private const uint NextButtonId = 0x4903;
    private readonly Dispatcher _dispatcher;
    private readonly Action _toggle;
    private readonly Action _previous;
    private readonly Action _next;
    private readonly Action<double> _seek;
    private readonly IntPtr _windowHandle;
    private readonly HwndSource _windowSource;
    private readonly uint _taskbarCreatedMessage;
    private readonly List<IntPtr> _icons = [];
    private ITaskbarList3? _taskbar;
    private SystemMediaTransportControls? _transport;
    private bool _thumbnailButtonsAdded;
    private bool? _lastPlaying;
    private TaskbarProgressState _lastProgressState = unchecked((TaskbarProgressState)uint.MaxValue);
    private long _lastProgress = -1;
    private long _lastDuration = -1;
    private string? _lastTrackId;
    private long _lastTimelineSecond = -1;
    private bool? _lastTransportEnabled;
    private bool? _lastPreviousEnabled;
    private bool? _lastNextEnabled;
    private MediaPlaybackStatus? _lastMediaStatus;
    private bool _disposed;
    private IntPtr _previousIcon;
    private IntPtr _playIcon;
    private IntPtr _pauseIcon;
    private IntPtr _nextIcon;

    public WindowsMediaIntegration(Window window, Action toggle, Action previous, Action next, Action<double> seek)
    {
        _dispatcher = window.Dispatcher;
        _toggle = toggle;
        _previous = previous;
        _next = next;
        _seek = seek;
        _windowHandle = new WindowInteropHelper(window).Handle;
        _windowSource = HwndSource.FromHwnd(_windowHandle) ?? throw new InvalidOperationException("Windows could not attach taskbar controls.");
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarButtonCreated");
        _windowSource.AddHook(WindowMessage);
        InitializeTaskbar();
        InitializeSystemMediaControls();
        _dispatcher.BeginInvoke(AddThumbnailButtons, DispatcherPriority.ApplicationIdle);
    }

    public void Update(WindowsMediaState state)
    {
        if (_disposed) return;
        UpdateThumbnailButton(state);
        UpdateTaskbarProgress(state);
        UpdateSystemMediaControls(state);
    }

    private void InitializeTaskbar()
    {
        try
        {
            _taskbar = (ITaskbarList3)(object)new TaskbarList();
            Marshal.ThrowExceptionForHR(_taskbar.HrInit());
            var color = UsesLightTaskbar() ? Color.FromArgb(255, 69, 55, 92) : Color.FromArgb(255, 225, 218, 239);
            _previousIcon = CreateTaskbarIcon(TaskbarIcon.Previous, color);
            _playIcon = CreateTaskbarIcon(TaskbarIcon.Play, color);
            _pauseIcon = CreateTaskbarIcon(TaskbarIcon.Pause, color);
            _nextIcon = CreateTaskbarIcon(TaskbarIcon.Next, color);
        }
        catch
        {
            _taskbar = null;
        }
    }

    private void AddThumbnailButtons()
    {
        if (_taskbar is null || _thumbnailButtonsAdded || _disposed) return;
        try
        {
            var buttons = new[]
            {
                CreateThumbnailButton(PreviousButtonId, _previousIcon, "Previous"),
                CreateThumbnailButton(PlayButtonId, _playIcon, "Play"),
                CreateThumbnailButton(NextButtonId, _nextIcon, "Next")
            };
            Marshal.ThrowExceptionForHR(_taskbar.ThumbBarAddButtons(_windowHandle, (uint)buttons.Length, buttons));
            _thumbnailButtonsAdded = true;
        }
        catch { }
    }

    private void UpdateThumbnailButton(WindowsMediaState state)
    {
        if (_taskbar is null || !_thumbnailButtonsAdded || _lastPlaying == state.IsPlaying) return;
        try
        {
            var button = CreateThumbnailButton(PlayButtonId, state.IsPlaying ? _pauseIcon : _playIcon, state.IsPlaying ? "Pause" : "Play");
            Marshal.ThrowExceptionForHR(_taskbar.ThumbBarUpdateButtons(_windowHandle, 1, [button]));
            _lastPlaying = state.IsPlaying;
        }
        catch { }
    }

    private void UpdateTaskbarProgress(WindowsMediaState state)
    {
        if (_taskbar is null) return;
        var progressState = state.Track is null || state.Duration <= TimeSpan.Zero
            ? TaskbarProgressState.NoProgress
            : state.IsLoading ? TaskbarProgressState.Indeterminate
            : state.IsPlaying ? TaskbarProgressState.Normal : TaskbarProgressState.Paused;
        var progress = Math.Max(0, (long)state.Position.TotalMilliseconds);
        var duration = Math.Max(1, (long)state.Duration.TotalMilliseconds);
        if (progressState == _lastProgressState &&
            (progressState is TaskbarProgressState.NoProgress or TaskbarProgressState.Indeterminate ||
             progress / 500 == _lastProgress / 500 && duration == _lastDuration)) return;
        try
        {
            if (progressState is TaskbarProgressState.Normal or TaskbarProgressState.Paused or TaskbarProgressState.Error)
                Marshal.ThrowExceptionForHR(_taskbar.SetProgressValue(_windowHandle, (ulong)Math.Min(progress, duration), (ulong)duration));
            Marshal.ThrowExceptionForHR(_taskbar.SetProgressState(_windowHandle, progressState));
            _lastProgressState = progressState;
            _lastProgress = progress;
            _lastDuration = duration;
        }
        catch { }
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

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)message == _taskbarCreatedMessage)
        {
            _thumbnailButtonsAdded = false;
            AddThumbnailButtons();
        }
        else if (message == WmCommand && ((wParam.ToInt64() >> 16) & 0xffff) == ThumbnailButtonClicked)
        {
            var id = (uint)(wParam.ToInt64() & 0xffff);
            if (id == PreviousButtonId) _dispatcher.BeginInvoke(_previous);
            else if (id == PlayButtonId) _dispatcher.BeginInvoke(_toggle);
            else if (id == NextButtonId) _dispatcher.BeginInvoke(_next);
            handled = id is PreviousButtonId or PlayButtonId or NextButtonId;
        }
        return IntPtr.Zero;
    }

    private static ThumbnailButton CreateThumbnailButton(uint id, IntPtr icon, string tooltip) => new()
    {
        Mask = ThumbnailButtonMask.Icon | ThumbnailButtonMask.Tooltip | ThumbnailButtonMask.Flags,
        Id = id,
        Icon = icon,
        Tooltip = tooltip,
        Flags = ThumbnailButtonFlags.Enabled
    };

    private IntPtr CreateTaskbarIcon(TaskbarIcon icon, Color color)
    {
        using var bitmap = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(color, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(color);
        switch (icon)
        {
            case TaskbarIcon.Previous:
                graphics.DrawLine(pen, 9, 8, 9, 24);
                graphics.DrawLines(pen, [new PointF(23, 8), new PointF(13, 16), new PointF(23, 24)]);
                break;
            case TaskbarIcon.Play:
                graphics.FillPolygon(brush, [new PointF(11, 7), new PointF(24, 16), new PointF(11, 25)]);
                break;
            case TaskbarIcon.Pause:
                graphics.FillRectangle(brush, 10, 7, 4, 18);
                graphics.FillRectangle(brush, 19, 7, 4, 18);
                break;
            case TaskbarIcon.Next:
                graphics.DrawLines(pen, [new PointF(9, 8), new PointF(19, 16), new PointF(9, 24)]);
                graphics.DrawLine(pen, 23, 8, 23, 24);
                break;
        }
        var handle = bitmap.GetHicon();
        _icons.Add(handle);
        return handle;
    }

    private static bool UsesLightTaskbar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _windowSource.RemoveHook(WindowMessage);
        if (_transport is not null)
        {
            _transport.ButtonPressed -= TransportButtonPressed;
            _transport.PlaybackPositionChangeRequested -= PlaybackPositionChangeRequested;
            try { _transport.IsEnabled = false; } catch { }
            _transport = null;
        }
        if (_taskbar is not null)
        {
            try { _taskbar.SetProgressState(_windowHandle, TaskbarProgressState.NoProgress); } catch { }
            try { Marshal.FinalReleaseComObject(_taskbar); } catch { }
            _taskbar = null;
        }
        foreach (var icon in _icons) if (icon != IntPtr.Zero) DestroyIcon(icon);
        _icons.Clear();
    }

    private enum TaskbarIcon { Previous, Play, Pause, Next }

    [Flags]
    private enum ThumbnailButtonMask : uint { Bitmap = 0x1, Icon = 0x2, Tooltip = 0x4, Flags = 0x8 }
    private enum ThumbnailButtonFlags : uint { Enabled = 0, Disabled = 0x1, DismissOnClick = 0x2, NoBackground = 0x4, Hidden = 0x8, NonInteractive = 0x10 }
    private enum TaskbarProgressState : uint { NoProgress = 0, Indeterminate = 0x1, Normal = 0x2, Error = 0x4, Paused = 0x8 }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ThumbnailButton
    {
        public ThumbnailButtonMask Mask;
        public uint Id;
        public uint Bitmap;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Tooltip;
        public ThumbnailButtonFlags Flags;
    }

    [ComImport, Guid("56FDF344-FD6D-11D0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
    private sealed class TaskbarList { }

    [ComImport, Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEA84"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        [PreserveSig] int HrInit();
        [PreserveSig] int AddTab(IntPtr hwnd);
        [PreserveSig] int DeleteTab(IntPtr hwnd);
        [PreserveSig] int ActivateTab(IntPtr hwnd);
        [PreserveSig] int SetActiveAlt(IntPtr hwnd);
        [PreserveSig] int MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        [PreserveSig] int SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        [PreserveSig] int SetProgressState(IntPtr hwnd, TaskbarProgressState state);
        [PreserveSig] int RegisterTab(IntPtr tab, IntPtr mdi);
        [PreserveSig] int UnregisterTab(IntPtr tab);
        [PreserveSig] int SetTabOrder(IntPtr tab, IntPtr insertBefore);
        [PreserveSig] int SetTabActive(IntPtr tab, IntPtr mdi, uint reserved);
        [PreserveSig] int ThumbBarAddButtons(IntPtr hwnd, uint count, [In] ThumbnailButton[] buttons);
        [PreserveSig] int ThumbBarUpdateButtons(IntPtr hwnd, uint count, [In] ThumbnailButton[] buttons);
        [PreserveSig] int ThumbBarSetImageList(IntPtr hwnd, IntPtr imageList);
        [PreserveSig] int SetOverlayIcon(IntPtr hwnd, IntPtr icon, [MarshalAs(UnmanagedType.LPWStr)] string description);
        [PreserveSig] int SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string tooltip);
        [PreserveSig] int SetThumbnailClip(IntPtr hwnd, IntPtr clip);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr icon);
}
