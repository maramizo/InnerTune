using System.Diagnostics;
using System.Text.Json;
using System.IO;
using System.Text;

namespace InnerTune;

public sealed class ProviderService : IDisposable
{
    private readonly string _script = Path.Combine(AppContext.BaseDirectory, "provider", "provider.mjs");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly System.Threading.Timer _idleTimer;
    private readonly TimeSpan _idleTimeout;
    private Process? _process;

    public ProviderService(TimeSpan? idleTimeout = null)
    {
        _idleTimeout = idleTimeout ?? TimeSpan.FromSeconds(30);
        _idleTimer = new(_ => Stop(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public Task<IReadOnlyList<Track>> SearchAsync(string query, CancellationToken token = default) =>
        SendAsync<IReadOnlyList<Track>>("search", query, token);

    public Task<HomeDiscovery> HomeAsync(string? seedVideoId, bool refresh = false, CancellationToken token = default) =>
        SendAsync<HomeDiscovery>("home", new { seedVideoId, refresh }, token);

    public Task<HomeDiscovery> MoodAsync(string name, bool refresh = false, CancellationToken token = default) =>
        SendAsync<HomeDiscovery>("mood", new { name, refresh }, token);

    public Task<CollectionDetail> CollectionAsync(string kind, string id, CancellationToken token = default) =>
        SendAsync<CollectionDetail>("collection", new { kind, id }, token);

    public async Task<string> ResolveAsync(string videoId, CancellationToken token = default) =>
        (await SendAsync<ResolveResult>("resolve", videoId, token)).Url;

    public Task<VideoResult> ResolveVideoAsync(string videoId, CancellationToken token = default) =>
        SendAsync<VideoResult>("video", videoId, token);

    public Task<IReadOnlyList<VideoCandidate>> FindVideoCandidatesAsync(Track track, string? query = null, CancellationToken token = default) =>
        SendAsync<IReadOnlyList<VideoCandidate>>("video_candidates", new { track, query }, token);

    public async Task DownloadAsync(string videoId, string destination, CancellationToken token = default) =>
        _ = await SendAsync<DownloadResult>("download", new { videoId, destination }, token);

    private async Task<T> SendAsync<T>(string command, object argument, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            EnsureStarted();
            _idleTimer.Change(Timeout.Infinite, Timeout.Infinite);
            var id = Guid.NewGuid().ToString("N");
            var request = JsonSerializer.Serialize(new { id, command, argument });
            await _process!.StandardInput.WriteLineAsync(request.AsMemory(), token);
            await _process.StandardInput.FlushAsync(token);
            using var registration = token.Register(Stop);
            var line = await _process.StandardOutput.ReadLineAsync(token);
            if (line is null) throw new InvalidOperationException("The InnerTube provider stopped unexpectedly.");
            using var response = JsonDocument.Parse(line);
            if (response.RootElement.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.GetString() ?? "InnerTube provider failed.");
            var result = response.RootElement.GetProperty("result").Deserialize<T>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return result ?? throw new InvalidOperationException("InnerTube returned no data.");
        }
        finally
        {
            _idleTimer.Change(_idleTimeout, Timeout.InfiniteTimeSpan);
            _gate.Release();
        }
    }

    private void EnsureStarted()
    {
        if (_process is { HasExited: false }) return;
        if (!File.Exists(_script)) throw new InvalidOperationException("The InnerTube provider is missing. Run setup.ps1 once.");
        var start = new ProcessStartInfo
        {
            FileName = RuntimeTools.Node, UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(_script)!
        };
        start.ArgumentList.Add(_script); start.ArgumentList.Add("serve");
        start.Environment[AppRuntime.DataDirectoryVariable] = AppRuntime.DataDirectory;
        _process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Node.js.");
        var process = _process;
        _ = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                Debug.WriteLine(line);
                AppRuntime.TestLog($"provider: {line}");
            }
        });
    }

    private void Stop()
    {
        try { if (_process is { HasExited: false }) _process.Kill(true); } catch { }
        try { _process?.Dispose(); } catch { }
        _process = null;
    }

    public void Dispose() { _idleTimer.Dispose(); Stop(); _gate.Dispose(); }
    private sealed class ResolveResult { public string Url { get; set; } = ""; }
    private sealed class DownloadResult { public string Path { get; set; } = ""; }
}

public sealed class VideoResult
{
    public string Url { get; set; } = "";
    public string Quality { get; set; } = "";
}
