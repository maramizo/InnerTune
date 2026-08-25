using System.Text.Json;
using InnerTune;

if (args.Length == 1)
{
    var imported = PlaylistShareCodec.Decode(args[0]);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        passed = true,
        imported.Name,
        tracks = imported.Tracks.Count,
        firstTrackId = imported.Tracks.FirstOrDefault()?.Id
    }));
    return;
}

var source = new[]
{
    new Track { Id = "dQw4w9WgXcQ", Title = "Test song", Artist = "Test artist", Album = "Test album", DurationSeconds = 213, DurationText = "3:33" },
    new Track { Id = "9bZkp7q19f0", Title = "Second song", Artist = "Another artist", DurationSeconds = 252, DurationText = "4:12" }
};
var link = PlaylistShareCodec.Encode("Road Trip / 2026", source);
var decoded = PlaylistShareCodec.Decode(link);
if (decoded.Name != "Road Trip / 2026" || decoded.Tracks.Count != source.Length ||
    !decoded.Tracks.Select(track => track.Id).SequenceEqual(source.Select(track => track.Id)))
    throw new InvalidOperationException("Playlist round trip did not preserve its name and tracks.");
if (!PlaylistShareCodec.IsPlaylistLink(link)) throw new InvalidOperationException("Generated link was not recognized.");
try
{
    _ = PlaylistShareCodec.Decode("innertune://playlist/v1/not-valid");
    throw new InvalidOperationException("Damaged playlist data was accepted.");
}
catch (InvalidOperationException error) when (error.Message != "Damaged playlist data was accepted.") { }

Console.WriteLine(JsonSerializer.Serialize(new
{
    passed = true,
    link,
    linkPrefix = link[..Math.Min(32, link.Length)],
    linkLength = link.Length,
    decoded.Name,
    tracks = decoded.Tracks.Select(track => new { track.Id, track.Title, track.Artist, track.DurationSeconds })
}));
