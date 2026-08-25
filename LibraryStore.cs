using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;

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

    public string DataDirectory { get; } = AppRuntime.DataDirectory;
    public string FilePath => Path.Combine(DataDirectory, "library.json");

    public async Task<LibraryData> LoadAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return await LoadUnlockedAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(LibraryData data)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await SaveUnlockedAsync(data).ConfigureAwait(false); }
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
        if (!File.Exists(FilePath))
        {
            var empty = new LibraryData();
            await SaveUnlockedAsync(empty).ConfigureAwait(false);
            return empty;
        }
        await using var stream = File.Open(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return await JsonSerializer.DeserializeAsync<LibraryData>(stream, _json).ConfigureAwait(false) ?? new LibraryData();
    }

    private async Task SaveUnlockedAsync(LibraryData data)
    {
        Directory.CreateDirectory(DataDirectory);
        data.UpdatedAt = DateTimeOffset.Now;
        var temporary = FilePath + ".tmp";
        await using (var stream = File.Create(temporary)) await JsonSerializer.SerializeAsync(stream, data, _json).ConfigureAwait(false);
        File.Move(temporary, FilePath, true);
    }
}
