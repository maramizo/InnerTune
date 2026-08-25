using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace InnerTune;

public sealed partial class LyricsService : IDisposable
{
    private readonly string _cacheDirectory;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    public LyricsService(string dataDirectory)
    {
        _cacheDirectory = Path.Combine(dataDirectory, "lyrics-cache");
        _http = new HttpClient { BaseAddress = new Uri("https://lrclib.net/"), Timeout = TimeSpan.FromSeconds(25) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Lrclib-Client", "InnerTune v1.0 (personal local Windows music player)");
    }

    public async Task<LyricsDocument> GetAsync(Track track, bool refresh = false, CancellationToken token = default)
    {
        Directory.CreateDirectory(_cacheDirectory);
        var cachePath = Path.Combine(_cacheDirectory, $"{SafeFileName(track.Id)}.json");
        if (!refresh && await ReadCacheAsync(cachePath, token).ConfigureAwait(false) is { } cached) return cached;

        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!refresh && await ReadCacheAsync(cachePath, token).ConfigureAwait(false) is { } secondCheck) return secondCheck;
            var document = await FetchAsync(track, token).ConfigureAwait(false);
            var temporary = cachePath + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(document, _json), Utf8NoBom, token).ConfigureAwait(false);
            File.Move(temporary, cachePath, true);
            return document;
        }
        finally { _gate.Release(); }
    }

    private async Task<LyricsDocument?> ReadCacheAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            var cached = await JsonSerializer.DeserializeAsync<LyricsDocument>(stream, _json, token).ConfigureAwait(false);
            if (cached is null) return null;
            var lifetime = cached.Found ? TimeSpan.FromDays(180) : TimeSpan.FromDays(1);
            return DateTimeOffset.UtcNow - cached.FetchedAt <= lifetime ? cached : null;
        }
        catch (Exception error) when (error is IOException or JsonException) { return null; }
    }

    private async Task<LyricsDocument> FetchAsync(Track track, CancellationToken token)
    {
        var exactQuery = new Dictionary<string, string?>
        {
            ["track_name"] = track.Title,
            ["artist_name"] = track.Artist,
            ["album_name"] = track.Album,
            ["duration"] = track.DurationSeconds > 0 ? track.DurationSeconds.ToString(CultureInfo.InvariantCulture) : null
        };
        var exact = await GetRecordAsync("api/get", exactQuery, token).ConfigureAwait(false);
        if (exact is not null) return ToDocument(track, exact);

        await Task.Delay(250, token).ConfigureAwait(false);
        var searchQuery = new Dictionary<string, string?> { ["track_name"] = track.Title, ["artist_name"] = track.Artist };
        var results = await GetRecordsAsync("api/search", searchQuery, token).ConfigureAwait(false);
        var best = results
            .Select(record => (Record: record, Score: MatchScore(track, record)))
            .Where(candidate => candidate.Score >= 9)
            .OrderByDescending(candidate => candidate.Score)
            .FirstOrDefault().Record;
        return best is null ? Missing(track) : ToDocument(track, best);
    }

    private async Task<LrclibRecord?> GetRecordAsync(string endpoint, IReadOnlyDictionary<string, string?> query, CancellationToken token)
    {
        using var response = await SendAsync(BuildUri(endpoint, query), token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<LrclibRecord>(stream, _json, token).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<LrclibRecord>> GetRecordsAsync(string endpoint, IReadOnlyDictionary<string, string?> query, CancellationToken token)
    {
        using var response = await SendAsync(BuildUri(endpoint, query), token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return [];
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<List<LrclibRecord>>(stream, _json, token).ConfigureAwait(false) ?? [];
    }

    private async Task<HttpResponseMessage> SendAsync(string uri, CancellationToken token)
    {
        var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if ((int)response.StatusCode != 429) return response;
        var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
        response.Dispose();
        await Task.Delay(delay > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delay, token).ConfigureAwait(false);
        return await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
    }

    private static LyricsDocument ToDocument(Track track, LrclibRecord record)
    {
        var synced = ParseSynced(record.SyncedLyrics);
        var lines = synced.Count > 0 ? synced : ParsePlain(record.PlainLyrics);
        return new LyricsDocument
        {
            TrackId = track.Id,
            TrackName = record.TrackName ?? track.Title,
            ArtistName = record.ArtistName ?? track.Artist,
            Found = record.Instrumental || lines.Count > 0,
            IsInstrumental = record.Instrumental,
            IsSynced = synced.Count > 0,
            Lines = lines,
            FetchedAt = DateTimeOffset.UtcNow
        };
    }

    private static LyricsDocument Missing(Track track) => new()
    {
        TrackId = track.Id, TrackName = track.Title, ArtistName = track.Artist, FetchedAt = DateTimeOffset.UtcNow
    };

    private static List<LyricsLine> ParseSynced(string? lyrics)
    {
        var lines = new List<LyricsLine>();
        if (string.IsNullOrWhiteSpace(lyrics)) return lines;
        foreach (var rawLine in lyrics.ReplaceLineEndings("\n").Split('\n'))
        {
            var matches = TimestampRegex().Matches(rawLine);
            if (matches.Count == 0) continue;
            var text = TimestampRegex().Replace(rawLine, "").Trim();
            foreach (Match match in matches)
            {
                var minutes = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                var seconds = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                var fractionText = match.Groups[3].Success ? match.Groups[3].Value : "0";
                var milliseconds = int.Parse(fractionText.PadRight(3, '0')[..3], CultureInfo.InvariantCulture);
                lines.Add(new() { Text = text.Length == 0 ? "♪" : text, StartSeconds = minutes * 60 + seconds + milliseconds / 1000d });
            }
        }
        return lines.OrderBy(line => line.StartSeconds).ToList();
    }

    private static List<LyricsLine> ParsePlain(string? lyrics) => string.IsNullOrWhiteSpace(lyrics)
        ? []
        : lyrics.ReplaceLineEndings("\n").Split('\n').Select(line => new LyricsLine { Text = line.Length == 0 ? " " : line }).ToList();

    private static int MatchScore(Track track, LrclibRecord record)
    {
        var title = Normalize(track.Title); var candidateTitle = Normalize(record.TrackName ?? "");
        var artist = Normalize(track.Artist); var candidateArtist = Normalize(record.ArtistName ?? "");
        var score = title.Length > 0 && title == candidateTitle ? 8 : (ContainsEither(title, candidateTitle) ? 4 : 0);
        score += artist.Length > 0 && artist == candidateArtist ? 6 : (ContainsEither(artist, candidateArtist) ? 3 : 0);
        var durationDifference = Math.Abs(track.DurationSeconds - record.Duration);
        if (track.DurationSeconds > 0) score += durationDifference <= 2 ? 6 : durationDifference <= 6 ? 3 : 0;
        if (!string.IsNullOrWhiteSpace(track.Album) && Normalize(track.Album) == Normalize(record.AlbumName ?? "")) score += 2;
        return score;
    }

    private static string BuildUri(string endpoint, IReadOnlyDictionary<string, string?> values) => endpoint + "?" + string.Join("&",
        values.Where(pair => !string.IsNullOrWhiteSpace(pair.Value)).Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}"));
    private static bool ContainsEither(string left, string right) => left.Length > 0 && right.Length > 0 && (left.Contains(right) || right.Contains(left));
    private static string Normalize(string value) => new(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    private static string SafeFileName(string value) => string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));

    public void Dispose() { _http.Dispose(); _gate.Dispose(); }

    [GeneratedRegex(@"\[(\d{1,3}):(\d{2})(?:[\.:](\d{1,3}))?\]", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampRegex();

    private sealed class LrclibRecord
    {
        public string? TrackName { get; set; }
        public string? ArtistName { get; set; }
        public string? AlbumName { get; set; }
        public double Duration { get; set; }
        public bool Instrumental { get; set; }
        public string? PlainLyrics { get; set; }
        public string? SyncedLyrics { get; set; }
    }
}
