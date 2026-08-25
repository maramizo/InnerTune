using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace InnerTune;

public sealed class Track : INotifyPropertyChanged
{
    private bool _isFavorite;

    public string Id { get; set; } = "";
    public string Title { get; set; } = "Unknown title";
    public string Artist { get; set; } = "Unknown artist";
    public string? Album { get; set; }
    public int DurationSeconds { get; set; }
    public string DurationText { get; set; } = "--:--";
    public string? ArtworkUrl { get; set; }
    [JsonIgnore] public string Number { get; set; } = "";
    [JsonIgnore] public string Subtitle => string.IsNullOrWhiteSpace(Album) ? Artist : $"{Artist}  ·  {Album}";
    [JsonIgnore] public bool IsPlaying { get; set; }
    [JsonIgnore]
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value) return;
            _isFavorite = value;
            PropertyChanged?.Invoke(this, new(nameof(IsFavorite)));
            PropertyChanged?.Invoke(this, new(nameof(FavoriteGlyph)));
            PropertyChanged?.Invoke(this, new(nameof(FavoriteTooltip)));
        }
    }
    [JsonIgnore] public string FavoriteGlyph => IsFavorite ? "♥" : "♡";
    [JsonIgnore] public string FavoriteTooltip => IsFavorite ? "Unlike song" : "Like song";
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh()
    {
        PropertyChanged?.Invoke(this, new(nameof(Number)));
        PropertyChanged?.Invoke(this, new(nameof(IsPlaying)));
        PropertyChanged?.Invoke(this, new(nameof(IsFavorite)));
        PropertyChanged?.Invoke(this, new(nameof(FavoriteGlyph)));
        PropertyChanged?.Invoke(this, new(nameof(FavoriteTooltip)));
    }
}

public sealed class LibraryFolder
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "Folder";
    public string? ParentId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class Favorite
{
    public Track Track { get; set; } = new();
    public string? FolderId { get; set; }
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.Now;
    [JsonIgnore] public string FolderPath { get; set; } = "Favorites";
}

public sealed class SavedQueue : INotifyPropertyChanged
{
    private bool _isActive;
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "Queue";
    public string? FolderId { get; set; }
    public List<Track> Tracks { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    [JsonIgnore] public string DisplayPath { get; set; } = "";
    [JsonIgnore] public string Summary => $"{(string.IsNullOrWhiteSpace(DisplayPath) ? Name : DisplayPath)}  ·  {Tracks.Count} songs";
    [JsonIgnore] public string? CoverUrl => Tracks.FirstOrDefault()?.ArtworkUrl;
    [JsonIgnore]
    public bool IsActive
    {
        get => _isActive;
        set { if (_isActive == value) return; _isActive = value; PropertyChanged?.Invoke(this, new(nameof(IsActive))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class PlaybackCommand
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Type { get; set; } = "";
    public int? Index { get; set; }
    public double? Seconds { get; set; }
    public double? Value { get; set; }
    public bool? Enabled { get; set; }
    public string? Mode { get; set; }
}

public sealed class PlaybackSnapshot
{
    public string Status { get; set; } = "idle";
    public Track? Track { get; set; }
    public string? TrackId { get; set; }
    public int QueueIndex { get; set; } = -1;
    public string? QueueId { get; set; }
    public string QueueName { get; set; } = "Current queue";
    public double PositionSeconds { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class LyricsLine : INotifyPropertyChanged
{
    private bool _isActive;
    public string Text { get; set; } = "";
    public double? StartSeconds { get; set; }
    [JsonIgnore]
    public bool IsActive
    {
        get => _isActive;
        set { if (_isActive == value) return; _isActive = value; PropertyChanged?.Invoke(this, new(nameof(IsActive))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class LyricsDocument
{
    public string TrackId { get; set; } = "";
    public string TrackName { get; set; } = "";
    public string ArtistName { get; set; } = "";
    public bool Found { get; set; }
    public bool IsInstrumental { get; set; }
    public bool IsSynced { get; set; }
    public List<LyricsLine> Lines { get; set; } = [];
    public DateTimeOffset FetchedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class HistoryEntry
{
    public Track Track { get; set; } = new();
    public DateTimeOffset LastPlayedAt { get; set; } = DateTimeOffset.Now;
    public int PlayCount { get; set; } = 1;
}

public sealed class DiscoveryItem
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "song";
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string? ArtworkUrl { get; set; }
    public Track? Track { get; set; }
    [JsonIgnore] public string PrimaryAction => Kind == "song" ? "Play" : "Open";
    [JsonIgnore] public bool CanRemoveFromHistory { get; set; }
}

public sealed class DiscoverySection
{
    public string Title { get; set; } = "";
    public List<DiscoveryItem> Items { get; set; } = [];
}

public sealed class HomeDiscovery
{
    public List<DiscoverySection> Sections { get; set; } = [];
    public List<string> Moods { get; set; } = [];
    public DateTimeOffset FetchedAt { get; set; } = DateTimeOffset.Now;
    public bool Stale { get; set; }
}

public sealed class CollectionDetail
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "playlist";
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string? ArtworkUrl { get; set; }
    public List<Track> Tracks { get; set; } = [];
    [JsonIgnore] public string CountText => $"{Tracks.Count} {(Tracks.Count == 1 ? "song" : "songs")}";
}

public sealed class VideoCandidate
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Author { get; set; } = "YouTube";
    public string? ThumbnailUrl { get; set; }
    public int DurationSeconds { get; set; }
    public string DurationText { get; set; } = "--:--";
    public int Score { get; set; }
    public string Kind { get; set; } = "Video";
    public bool UseVideoAudio { get; set; }
    public bool Recommended { get; set; }
    [JsonIgnore] public string PlaybackLabel => UseVideoAudio ? "Uses video audio" : "Synced to song audio";
    [JsonIgnore] public string MatchLabel => Recommended ? "BEST MATCH" : Kind.ToUpperInvariant();
}

public sealed class VideoSelection
{
    public string VideoId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Author { get; set; } = "YouTube";
    public string? ThumbnailUrl { get; set; }
    public int DurationSeconds { get; set; }
    public string Kind { get; set; } = "Video";
    public bool UseVideoAudio { get; set; }
    public DateTimeOffset SelectedAt { get; set; } = DateTimeOffset.Now;

    public static VideoSelection FromCandidate(VideoCandidate candidate) => new()
    {
        VideoId = candidate.Id,
        Title = candidate.Title,
        Author = candidate.Author,
        ThumbnailUrl = candidate.ThumbnailUrl,
        DurationSeconds = candidate.DurationSeconds,
        Kind = candidate.Kind,
        UseVideoAudio = candidate.UseVideoAudio
    };
}

public sealed class LibraryData
{
    public int Version { get; set; } = 1;
    public double Volume { get; set; } = 72;
    public bool ShuffleEnabled { get; set; }
    public string RepeatMode { get; set; } = "off";
    public string? QueueSourceId { get; set; }
    public string QueueSourceName { get; set; } = "Current queue";
    public PlaybackSnapshot Playback { get; set; } = new();
    public ObservableCollection<Track> Queue { get; set; } = [];
    public ObservableCollection<LibraryFolder> Folders { get; set; } = [];
    public ObservableCollection<Favorite> Favorites { get; set; } = [];
    public ObservableCollection<SavedQueue> SavedQueues { get; set; } = [];
    public ObservableCollection<HistoryEntry> RecentlyPlayed { get; set; } = [];
    public Dictionary<string, VideoSelection> VideoMappings { get; set; } = [];
    public List<PlaybackCommand> PendingCommands { get; set; } = [];
    public AppSettings Settings { get; set; } = new();
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class AppSettings
{
    public string Theme { get; set; } = "midnight";
    public string Icon { get; set; } = "dj-cat";
    public string? CustomIconPath { get; set; }
    public bool AutoResumeOnStart { get; set; }
}

public sealed class ChatMessage
{
    public string Role { get; set; } = "Assistant";
    public string Text { get; set; } = "";
    public string Alignment => Role == "You" ? "Right" : "Left";
    public string Summary
    {
        get
        {
            var end = Text.IndexOfAny(['\r', '\n']);
            return end < 0 ? Text : Text[..end];
        }
    }
    public string Details
    {
        get
        {
            var end = Text.IndexOfAny(['\r', '\n']);
            return end < 0 ? "Completed successfully." : Text[(end + 1)..].TrimStart('\r', '\n');
        }
    }
}
