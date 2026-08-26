using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace InnerTune;

public enum PlaybackRepeatMode { Off, All, One }

public sealed class PlayerService : IDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly ProviderService _provider;
    private readonly string _audioCache = Path.Combine(AppRuntime.DataDirectory, "audio-cache");
    private readonly Dictionary<string, string> _preparedFiles = new();
    private readonly ConcurrentDictionary<string, Task<string>> _preparations = new();
    private readonly ConcurrentDictionary<string, TempoAnalysis> _tempoCache = new();
    private IReadOnlyList<Track> _queue = [];
    private string[] _queueIds = [];
    private WaveOutEvent? _output;
    private MediaFoundationReader? _reader;
    private RmsMeteringSampleProvider? _meter;
    private Track? _currentTrack;
    private int _index = -1;
    private bool _playing;
    private float _volume = .72f;
    private TimeSpan _pendingPosition;
    private CancellationTokenSource? _loadCancel;
    private CancellationTokenSource? _tempoCancel;
    private string? _currentSource;
    private readonly ShuffleNavigator _shuffle = new();
    private readonly List<string> _playNextIds = [];
    private bool _shuffleEnabled;

    public event EventHandler? StateChanged;
    public event EventHandler<string>? Failed;
    public event Action<float>? AudioLevelChanged;
    public event Action<string, double>? TempoEstimated;
    public event Action<string, double, double, double, IReadOnlyList<JumpWindow>>? MotionProfileEstimated;
    public Track? CurrentTrack => _currentTrack;
    public int CurrentIndex => _index;
    public bool IsPlaying => _playing;
    public bool IsLoading { get; private set; }
    public bool TempoAnalysisEnabled
    {
        get => _tempoAnalysisEnabled;
        set
        {
            if (_tempoAnalysisEnabled == value) return;
            _tempoAnalysisEnabled = value;
            if (!value) _tempoCancel?.Cancel();
            else if (_currentTrack is { } track && _currentSource is { } source) StartTempoAnalysis(track.Id, source);
        }
    }
    private bool _tempoAnalysisEnabled;
    public bool ShuffleEnabled
    {
        get => _shuffleEnabled;
        set
        {
            if (_shuffleEnabled == value) return;
            _shuffleEnabled = value;
            if (value) _shuffle.Reset(_queue.Count, _index);
            else _shuffle.Clear();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public PlaybackRepeatMode RepeatMode { get; set; }
    public TimeSpan Position => _reader?.CurrentTime ?? _pendingPosition;
    public TimeSpan Duration => _reader?.TotalTime ?? TimeSpan.FromSeconds(Math.Max(0, _currentTrack?.DurationSeconds ?? 0));
    public double Volume
    {
        get => _volume * 100;
        set
        {
            _volume = (float)(Math.Clamp(value, 0, 100) / 100);
            if (_output is not null) _output.Volume = _volume;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public PlayerService(ProviderService provider)
    {
        _provider = provider;
        _timer.Tick += (_, _) => StateChanged?.Invoke(this, EventArgs.Empty);
        _timer.Start();
    }

    public void SetQueue(IReadOnlyList<Track> queue)
    {
        var queueIds = queue.Select(track => track.Id).ToArray();
        var changed = !_queueIds.SequenceEqual(queueIds);
        _queueIds = queueIds;
        _queue = queue;
        if (_currentTrack is not null)
        {
            var newIndex = queue.ToList().FindIndex(track => track.Id == _currentTrack.Id);
            _index = newIndex;
        }
        if (changed && ShuffleEnabled) _shuffle.Reset(_queue.Count, _index);
        _playNextIds.RemoveAll(id => !queue.Any(track => track.Id == id));
    }

    public void PrioritizeNext(string trackId)
    {
        _playNextIds.RemoveAll(id => id == trackId);
        _playNextIds.Insert(0, trackId);
        _ = PrefetchNextAsync();
    }

    public void ClearPlayNext() => _playNextIds.Clear();

    public Task PlayAsync(int index)
    {
        if (index < 0 || index >= _queue.Count) return Task.CompletedTask;
        if (ShuffleEnabled) _shuffle.Reset(_queue.Count, index);
        else _shuffle.Clear();
        return StartTrackAsync(_queue[index], index, TimeSpan.Zero);
    }

    public void RestorePaused(Track track, int index, TimeSpan position)
    {
        _loadCancel?.Cancel();
        DisposePlayback();
        _index = index;
        _currentTrack = track;
        _pendingPosition = ClampPosition(position, track.DurationSeconds);
        _playing = false;
        IsLoading = false;
        if (ShuffleEnabled) _shuffle.Reset(_queue.Count, index);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task RestorePlayingAsync(Track track, int index, TimeSpan position)
    {
        if (ShuffleEnabled) _shuffle.Reset(_queue.Count, index);
        return StartTrackAsync(track, index, position);
    }

    private async Task StartTrackAsync(Track track, int index, TimeSpan startPosition)
    {
        _loadCancel?.Cancel();
        DisposePlayback();
        _loadCancel = new CancellationTokenSource();
        var load = _loadCancel;
        _index = index;
        _currentTrack = track;
        _pendingPosition = ClampPosition(startPosition, track.DurationSeconds);
        _playing = false;
        IsLoading = true;
        StateChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            if (!_preparedFiles.Remove(track.Id, out var source))
                source = await PrepareAudioAsync(track.Id, _loadCancel.Token);
            _reader = new MediaFoundationReader(source);
            _currentSource = source;
            _reader.CurrentTime = ClampPosition(_pendingPosition, (int)Math.Ceiling(_reader.TotalTime.TotalSeconds));
            const int animationSamplesPerSecond = 30;
            var samplesPerNotification = Math.Max(1,
                _reader.WaveFormat.SampleRate * _reader.WaveFormat.Channels / animationSamplesPerSecond);
            _meter = new RmsMeteringSampleProvider(_reader.ToSampleProvider(), samplesPerNotification);
            _meter.LoudnessAvailable += Meter_LoudnessAvailable;
            _output = new WaveOutEvent { DesiredLatency = 150, NumberOfBuffers = 3, Volume = _volume };
            _output.PlaybackStopped += Output_PlaybackStopped;
            _output.Init(_meter);
            _output.Play();
            _playing = true;
            StateChanged?.Invoke(this, EventArgs.Empty);
            _ = PrefetchNextAsync();
            StartTempoAnalysis(track.Id, source);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Failed?.Invoke(this, FriendlyPlaybackError(e)); }
        finally
        {
            if (ReferenceEquals(_loadCancel, load))
            {
                IsLoading = false;
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public void Toggle()
    {
        if (IsLoading) return;
        if (_currentTrack is null)
        {
            if (_queue.Count > 0) _ = PlayAsync(0);
            return;
        }
        if (_output is null) { _ = StartTrackAsync(_currentTrack, _index, _pendingPosition); return; }
        if (_playing) { _output.Pause(); _playing = false; AudioLevelChanged?.Invoke(0); }
        else { _output.Play(); _playing = true; }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Pause()
    {
        if (IsLoading)
        {
            _loadCancel?.Cancel();
            IsLoading = false;
        }
        if (_output is not null && _playing) _output.Pause();
        _playing = false;
        AudioLevelChanged?.Invoke(0);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Meter_LoudnessAvailable(float loudness) =>
        AudioLevelChanged?.Invoke(Math.Clamp(loudness, 0, 1));

    public async Task NextAsync(bool automatic = false)
    {
        if (_queue.Count == 0) return;
        var current = _currentTrack is null ? -1 : _queue.ToList().FindIndex(track => track.Id == _currentTrack.Id);
        while (_playNextIds.Count > 0)
        {
            var id = _playNextIds[0];
            _playNextIds.RemoveAt(0);
            var prioritized = Enumerable.Range(0, _queue.Count)
                .FirstOrDefault(index => index != current && _queue[index].Id == id, -1);
            if (prioritized < 0) continue;
            if (ShuffleEnabled) _shuffle.TakeSpecific(_queue.Count, current, prioritized);
            await StartTrackAsync(_queue[prioritized], prioritized, TimeSpan.Zero);
            return;
        }
        if (automatic && RepeatMode == PlaybackRepeatMode.One && current >= 0)
        {
            await StartTrackAsync(_queue[current], current, TimeSpan.Zero);
            return;
        }
        int next;
        if (ShuffleEnabled)
        {
            var allowNewCycle = !automatic || RepeatMode == PlaybackRepeatMode.All;
            if (!_shuffle.TryNext(_queue.Count, current, allowNewCycle, out next)) return;
        }
        else
        {
            if (automatic && RepeatMode == PlaybackRepeatMode.Off && current == _queue.Count - 1) return;
            next = (current + 1 + _queue.Count) % _queue.Count;
        }
        await StartTrackAsync(_queue[next], next, TimeSpan.Zero);
    }

    public async Task PreviousAsync()
    {
        if (Position.TotalSeconds > 4) { Seek(0); return; }
        if (_queue.Count == 0) return;
        var current = _currentTrack is null ? -1 : _queue.ToList().FindIndex(track => track.Id == _currentTrack.Id);
        var previous = ShuffleEnabled && _shuffle.TryPrevious(_queue.Count, current, out var shuffled)
            ? shuffled
            : current < 0 ? _queue.Count - 1 : (current - 1 + _queue.Count) % _queue.Count;
        await StartTrackAsync(_queue[previous], previous, TimeSpan.Zero);
    }

    public void Seek(double seconds)
    {
        _pendingPosition = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, Math.Max(0, Duration.TotalSeconds)));
        if (_reader is not null) _reader.CurrentTime = _pendingPosition;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static TimeSpan ClampPosition(TimeSpan position, int durationSeconds)
    {
        var maximum = Math.Max(0, durationSeconds);
        return TimeSpan.FromSeconds(Math.Clamp(position.TotalSeconds, 0, maximum));
    }

    public void SetUiUpdates(bool enabled) { if (enabled) _timer.Start(); else _timer.Stop(); }

    private void Output_PlaybackStopped(object? sender, StoppedEventArgs e)
    {
        _playing = false;
        AudioLevelChanged?.Invoke(0);
        StateChanged?.Invoke(this, EventArgs.Empty);
        if (e.Exception is not null) { Failed?.Invoke(this, FriendlyPlaybackError(e.Exception)); return; }
        if (_reader is not null && _reader.TotalTime > TimeSpan.Zero && _reader.CurrentTime >= _reader.TotalTime - TimeSpan.FromMilliseconds(500))
            _ = NextAsync(true);
    }

    private async Task PrefetchNextAsync()
    {
        if (_queue.Count < 2 || _currentTrack is null) return;
        var current = _queue.ToList().FindIndex(track => track.Id == _currentTrack.Id);
        var prioritized = _playNextIds.Select(id => Enumerable.Range(0, _queue.Count)
                .FirstOrDefault(index => index != current && _queue[index].Id == id, -1))
            .FirstOrDefault(index => index >= 0, -1);
        var next = _queue[prioritized >= 0 ? prioritized : (current + 1 + _queue.Count) % _queue.Count];
        if (_preparedFiles.ContainsKey(next.Id)) return;
        try { _preparedFiles[next.Id] = await PrepareAudioAsync(next.Id, CancellationToken.None); }
        catch { }
    }

    private async Task<string> PrepareAudioAsync(string videoId, CancellationToken token)
    {
        var task = _preparations.GetOrAdd(videoId, PrepareAudioCoreAsync);
        try { return await task.WaitAsync(token); }
        finally { if (task.IsCompleted) _preparations.TryRemove(videoId, out _); }
    }

    private async Task<string> PrepareAudioCoreAsync(string videoId)
    {
        Directory.CreateDirectory(_audioCache);
        var path = Path.Combine(_audioCache, $"{videoId}.playable.m4a");
        if (File.Exists(path) && new FileInfo(path).Length > 32_768)
        {
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
            return path;
        }

        var temporary = Path.Combine(_audioCache, $"{videoId}.source.m4a");
        try
        {
            var url = await _provider.ResolveAsync(videoId);
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "curl.exe"),
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in new[] { "--fail", "--location", "--silent", "--show-error", "--range", "0-", "--connect-timeout", "15", "--max-time", "180", "--output", temporary, url })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the Windows audio transfer.");
            LowerBackgroundPriority(process);
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var error = await errorTask;
            if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Audio download failed." : error.Trim());
            var remux = new ProcessStartInfo
            {
                FileName = RuntimeTools.Ffmpeg,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", temporary, "-map", "0:a:0", "-c", "copy", "-movflags", "+faststart", path })
                remux.ArgumentList.Add(argument);
            using var remuxProcess = Process.Start(remux) ?? throw new InvalidOperationException("FFmpeg is required to prepare this audio for Windows playback.");
            LowerBackgroundPriority(remuxProcess);
            var remuxErrorTask = remuxProcess.StandardError.ReadToEndAsync();
            await remuxProcess.WaitForExitAsync();
            var remuxError = await remuxErrorTask;
            if (remuxProcess.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(remuxError) ? "Windows audio preparation failed." : remuxError.Trim());
            File.Delete(temporary);
            _ = Task.Run(TrimAudioCache);
            return path;
        }
        catch
        {
            try { File.Delete(temporary); } catch { }
            try { File.Delete(path); } catch { }
            throw;
        }
    }

    private void StartTempoAnalysis(string trackId, string source)
    {
        _tempoCancel?.Cancel();
        _tempoCancel = null;
        if (!TempoAnalysisEnabled) return;
        if (_tempoCache.TryGetValue(trackId, out var cached))
        {
            TempoEstimated?.Invoke(trackId, cached.Bpm);
            MotionProfileEstimated?.Invoke(trackId, cached.Danceability, cached.FullnessFloor,
                cached.FullnessCeiling, cached.JumpWindows);
            return;
        }
        var cancellation = new CancellationTokenSource();
        _tempoCancel = cancellation;
        _ = AnalyzeTempoAsync(trackId, source, cancellation);
    }

    private async Task AnalyzeTempoAsync(string trackId, string source, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(750, cancellation.Token);
            var analysis = await LoadTempoAnalysisAsync(trackId, source, cancellation.Token);
            if (analysis is null)
            {
                analysis = await RepresentativeTempoAnalyzer.AnalyzeAsync(source, cancellation.Token);
                if (analysis is not null)
                    await SaveTempoAnalysisAsync(trackId, source, analysis, cancellation.Token);
            }
            if (analysis is null || cancellation.IsCancellationRequested) return;
            _tempoCache[trackId] = analysis;
            if (_currentTrack?.Id == trackId)
            {
                TempoEstimated?.Invoke(trackId, analysis.Bpm);
                MotionProfileEstimated?.Invoke(trackId, analysis.Danceability, analysis.FullnessFloor,
                    analysis.FullnessCeiling, analysis.JumpWindows);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            if (ReferenceEquals(_tempoCancel, cancellation)) _tempoCancel = null;
            cancellation.Dispose();
        }
    }

    private string TempoAnalysisPath(string trackId) => Path.Combine(_audioCache, $"{trackId}.motion-v6.json");

    private async Task<TempoAnalysis?> LoadTempoAnalysisAsync(string trackId, string source, CancellationToken token)
    {
        try
        {
            var sourceInfo = new FileInfo(source);
            var cached = JsonSerializer.Deserialize<CachedTempoAnalysis>(
                await File.ReadAllTextAsync(TempoAnalysisPath(trackId), token));
            return cached is not null && cached.SourceLength == sourceInfo.Length &&
                cached.SourceLastWriteUtcTicks == sourceInfo.LastWriteTimeUtc.Ticks
                ? cached.Analysis
                : null;
        }
        catch { return null; }
    }

    private async Task SaveTempoAnalysisAsync(string trackId, string source, TempoAnalysis analysis, CancellationToken token)
    {
        try
        {
            var sourceInfo = new FileInfo(source);
            var cached = new CachedTempoAnalysis(sourceInfo.Length, sourceInfo.LastWriteTimeUtc.Ticks, analysis);
            await File.WriteAllTextAsync(TempoAnalysisPath(trackId), JsonSerializer.Serialize(cached), token);
        }
        catch (OperationCanceledException) { throw; }
        catch { }
    }

    private void TrimAudioCache()
    {
        try
        {
            const long maximumBytes = 750L * 1024 * 1024;
            var files = new DirectoryInfo(_audioCache).GetFiles("*.playable.m4a").OrderByDescending(file => file.LastAccessTimeUtc).ToList();
            var total = files.Sum(file => file.Length);
            foreach (var file in files.AsEnumerable().Reverse())
            {
                if (total <= maximumBytes) break;
                try { total -= file.Length; file.Delete(); } catch { }
            }
        }
        catch { }
    }

    private static void LowerBackgroundPriority(Process process)
    {
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch { }
    }

    private sealed record CachedTempoAnalysis(long SourceLength, long SourceLastWriteUtcTicks, TempoAnalysis Analysis);

    private static string FriendlyPlaybackError(Exception error) =>
        error.Message.Contains("0x88890008", StringComparison.OrdinalIgnoreCase)
            ? "Windows could not open the selected audio device."
            : $"Couldn’t play this song: {error.Message}";

    private void DisposePlayback()
    {
        _tempoCancel?.Cancel();
        _tempoCancel = null;
        _currentSource = null;
        if (_meter is not null)
        {
            _meter.LoudnessAvailable -= Meter_LoudnessAvailable;
            _meter = null;
        }
        if (_output is not null)
        {
            _output.PlaybackStopped -= Output_PlaybackStopped;
            try { _output.Stop(); } catch { }
            _output.Dispose(); _output = null;
        }
        _reader?.Dispose(); _reader = null;
    }

    public void Dispose()
    {
        _timer.Stop(); _loadCancel?.Cancel(); DisposePlayback(); _provider.Dispose();
    }
}
