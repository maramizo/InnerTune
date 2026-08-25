using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InnerTune;

public sealed record SharedPlaylist(string Name, IReadOnlyList<Track> Tracks);

public static class PlaylistShareCodec
{
    public const string Scheme = "innertune";
    public const int MaxTracks = 500;
    public const int MaxUrlLength = 24_000;
    private const int MaxDecodedBytes = 2_000_000;
    private static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static string Encode(string name, IEnumerable<Track> tracks)
    {
        var cleanName = Clean(name, 120);
        if (cleanName.Length == 0) throw new InvalidOperationException("Give the playlist a name before sharing it.");
        var items = tracks.Take(MaxTracks + 1).Select(ToPayload).ToList();
        if (items.Count == 0) throw new InvalidOperationException("There are no songs to share.");
        if (items.Count > MaxTracks) throw new InvalidOperationException($"Playlist links support up to {MaxTracks} songs.");

        var json = JsonSerializer.SerializeToUtf8Bytes(new PlaylistPayload { Version = 1, Name = cleanName, Tracks = items }, JsonOptions);
        using var compressed = new MemoryStream();
        using (var brotli = new BrotliStream(compressed, CompressionLevel.SmallestSize, true)) brotli.Write(json);
        var payload = Convert.ToBase64String(compressed.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var link = $"{Scheme}://playlist/v1/{payload}";
        if (link.Length > MaxUrlLength)
            throw new InvalidOperationException("This playlist is too large for a reliable app link. Split it into smaller playlists and share those.");
        return link;
    }

    public static bool IsPlaylistLink(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) &&
        uri.Scheme.Equals(Scheme, StringComparison.OrdinalIgnoreCase) &&
        uri.Host.Equals("playlist", StringComparison.OrdinalIgnoreCase);

    public static SharedPlaylist Decode(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxUrlLength)
            throw new InvalidOperationException("That InnerTune playlist link is invalid or too large.");
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Scheme, StringComparison.OrdinalIgnoreCase) ||
            !uri.Host.Equals("playlist", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("That is not an InnerTune playlist link.");

        var segments = uri.AbsolutePath.Trim('/').Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2 || !segments[0].Equals("v1", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This playlist link uses an unsupported format.");

        byte[] packed;
        try
        {
            var base64 = segments[1].Replace('-', '+').Replace('_', '/');
            base64 += new string('=', (4 - base64.Length % 4) % 4);
            packed = Convert.FromBase64String(base64);
        }
        catch (FormatException) { throw new InvalidOperationException("The playlist link is damaged."); }

        byte[] json;
        try
        {
            using var input = new MemoryStream(packed);
            using var brotli = new BrotliStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = brotli.Read(buffer)) > 0)
            {
                if (output.Length + read > MaxDecodedBytes) throw new InvalidOperationException("The playlist data is too large.");
                output.Write(buffer, 0, read);
            }
            json = output.ToArray();
        }
        catch (InvalidDataException) { throw new InvalidOperationException("The playlist link is damaged."); }

        PlaylistPayload payload;
        try { payload = JsonSerializer.Deserialize<PlaylistPayload>(json, JsonOptions) ?? throw new JsonException(); }
        catch (JsonException) { throw new InvalidOperationException("The playlist link contains invalid data."); }
        if (payload.Version != 1 || payload.Tracks is not { Count: > 0 and <= MaxTracks })
            throw new InvalidOperationException("The playlist link contains an unsupported playlist.");

        var name = Clean(payload.Name, 120);
        if (name.Length == 0) throw new InvalidOperationException("The shared playlist has no name.");
        var tracks = payload.Tracks.Select(FromPayload).ToList();
        return new SharedPlaylist(name, tracks);
    }

    private static PlaylistTrackPayload ToPayload(Track track)
    {
        var id = Clean(track.Id, 128);
        if (id.Length == 0) throw new InvalidOperationException("A song in this playlist has no playable ID.");
        return new()
        {
            Id = id,
            Title = Clean(track.Title, 300),
            Artist = Clean(track.Artist, 300),
            Album = string.IsNullOrWhiteSpace(track.Album) ? null : Clean(track.Album, 300),
            Duration = Math.Clamp(track.DurationSeconds, 0, 86_400)
        };
    }

    private static Track FromPayload(PlaylistTrackPayload item)
    {
        var id = Clean(item.Id, 128);
        if (id.Length == 0) throw new InvalidOperationException("The shared playlist contains a song with no playable ID.");
        var duration = Math.Clamp(item.Duration, 0, 86_400);
        return new Track
        {
            Id = id,
            Title = Clean(item.Title, 300) is { Length: > 0 } title ? title : "Unknown title",
            Artist = Clean(item.Artist, 300) is { Length: > 0 } artist ? artist : "Unknown artist",
            Album = string.IsNullOrWhiteSpace(item.Album) ? null : Clean(item.Album, 300),
            DurationSeconds = duration,
            DurationText = TimeSpan.FromSeconds(duration).TotalHours >= 1
                ? TimeSpan.FromSeconds(duration).ToString(@"h\:mm\:ss")
                : TimeSpan.FromSeconds(duration).ToString(@"m\:ss"),
            ArtworkUrl = id.Length == 11 ? $"https://i.ytimg.com/vi/{Uri.EscapeDataString(id)}/hqdefault.jpg" : null
        };
    }

    private static string Clean(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? "" : new string(value.Trim().Where(character => !char.IsControl(character)).Take(max).ToArray());

    private sealed class PlaylistPayload
    {
        [JsonPropertyName("v")] public int Version { get; set; }
        [JsonPropertyName("n")] public string Name { get; set; } = "";
        [JsonPropertyName("t")] public List<PlaylistTrackPayload> Tracks { get; set; } = [];
    }

    private sealed class PlaylistTrackPayload
    {
        [JsonPropertyName("i")] public string Id { get; set; } = "";
        [JsonPropertyName("t")] public string Title { get; set; } = "";
        [JsonPropertyName("a")] public string Artist { get; set; } = "";
        [JsonPropertyName("l")] public string? Album { get; set; }
        [JsonPropertyName("d")] public int Duration { get; set; }
    }
}
