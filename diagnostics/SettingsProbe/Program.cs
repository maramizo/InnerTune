using System.Text.Json;
using InnerTune;

var settings = new AppSettings();
var playing = new PlaybackSnapshot { Status = "playing", TrackId = "song" };
var paused = new PlaybackSnapshot { Status = "paused", TrackId = "song" };
var defaultsAreSafe = settings.Theme == "midnight" && settings.Icon == "dj-cat" && !settings.AutoResumeOnStart;
var defaultDoesNotResume = !PlaybackRestorePolicy.ShouldAutoResume(playing, settings);
settings.AutoResumeOnStart = true;
var optInResumesOnlyPlaying = PlaybackRestorePolicy.ShouldAutoResume(playing, settings) &&
    !PlaybackRestorePolicy.ShouldAutoResume(paused, settings);
var roundTrip = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings));
var serializationWorks = roundTrip is { AutoResumeOnStart: true, Theme: "midnight", Icon: "dj-cat" };
var passed = defaultsAreSafe && defaultDoesNotResume && optInResumesOnlyPlaying && serializationWorks;
Console.WriteLine(JsonSerializer.Serialize(new
{
    passed,
    defaultTheme = new AppSettings().Theme,
    defaultIcon = new AppSettings().Icon,
    autoResumeDefault = new AppSettings().AutoResumeOnStart,
    optInResumesPlaying = optInResumesOnlyPlaying
}));
return passed ? 0 : 1;
