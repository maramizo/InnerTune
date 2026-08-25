using System.Text.Json;
using Windows.Media.Control;

var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
var sessions = new List<object>();
foreach (var session in manager.GetSessions())
{
    var properties = await session.TryGetMediaPropertiesAsync();
    var playback = session.GetPlaybackInfo();
    var timeline = session.GetTimelineProperties();
    sessions.Add(new
    {
        source = session.SourceAppUserModelId,
        properties.Title,
        properties.Artist,
        properties.AlbumTitle,
        status = playback.PlaybackStatus.ToString(),
        positionSeconds = timeline.Position.TotalSeconds,
        durationSeconds = (timeline.EndTime - timeline.StartTime).TotalSeconds
    });
}

Console.WriteLine(JsonSerializer.Serialize(new { sessions }));
