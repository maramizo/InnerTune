using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InnerTune;

public sealed class LibraryStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public LibraryStore(string? dataDirectory = null)
    {
        DataDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(dataDirectory) ? AppRuntime.DataDirectory : dataDirectory);
    }

    public string DataDirectory { get; }
    public string FilePath => Path.Combine(DataDirectory, "library.json");
    public string PlaybackFilePath => Path.Combine(DataDirectory, "playback.json");

    public async Task<LibraryData> LoadAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return await LoadUnlockedAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(LibraryData data)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await SaveUnlockedAsync(data).ConfigureAwait(false);
            await SavePlaybackUnlockedAsync(data.Playback).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task SavePlaybackAsync(PlaybackSnapshot playback)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await SavePlaybackUnlockedAsync(playback).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task<LibraryData> ReloadIfNewerAsync(LibraryData current)
    {
        if (!File.Exists(FilePath)) return current;
        var writeTime = File.GetLastWriteTimeUtc(FilePath);
        return writeTime > current.UpdatedAt.UtcDateTime ? await LoadAsync().ConfigureAwait(false) : current;
    }

    private async Task<LibraryData> LoadUnlockedAsync()
    {
        Directory.CreateDirectory(DataDirectory);
        LibraryData data;
        if (!File.Exists(FilePath))
        {
            data = new LibraryData();
            await SaveUnlockedAsync(data).ConfigureAwait(false);
        }
        else
        {
            await using var stream = File.Open(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            data = await JsonSerializer.DeserializeAsync<LibraryData>(stream, _json).ConfigureAwait(false) ?? new LibraryData();
        }
        if (File.Exists(PlaybackFilePath))
        {
            try
            {
                await using var playbackStream = File.Open(PlaybackFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                data.Playback = await JsonSerializer.DeserializeAsync<PlaybackSnapshot>(playbackStream, _json).ConfigureAwait(false) ?? data.Playback;
            }
            catch (JsonException) { }
            catch (IOException) { }
        }
        return data;
    }

    private async Task SaveUnlockedAsync(LibraryData data)
    {
        Directory.CreateDirectory(DataDirectory);
        data.UpdatedAt = DateTimeOffset.Now;
        var temporary = FilePath + ".tmp";
        await using (var stream = File.Create(temporary)) await JsonSerializer.SerializeAsync(stream, data, _json).ConfigureAwait(false);
        File.Move(temporary, FilePath, true);
    }

    private async Task SavePlaybackUnlockedAsync(PlaybackSnapshot playback)
    {
        Directory.CreateDirectory(DataDirectory);
        var temporary = PlaybackFilePath + ".tmp";
        await using (var stream = File.Create(temporary)) await JsonSerializer.SerializeAsync(stream, playback, _json).ConfigureAwait(false);
        File.Move(temporary, PlaybackFilePath, true);
    }
}
