using System.Text.Json;
using InnerTune;
using NAudio.Wave.SampleProviders;

var settings = new AppSettings();
var playing = new PlaybackSnapshot { Status = "playing", TrackId = "song" };
var paused = new PlaybackSnapshot { Status = "paused", TrackId = "song" };
var defaultsAreSafe = settings.Theme == "midnight" && settings.Icon == "dj-cat" && settings.AnimatedIconEnabled && !settings.AutoResumeOnStart;
var defaultDoesNotResume = !PlaybackRestorePolicy.ShouldAutoResume(playing, settings);
settings.AutoResumeOnStart = true;
var optInResumesOnlyPlaying = PlaybackRestorePolicy.ShouldAutoResume(playing, settings) &&
    !PlaybackRestorePolicy.ShouldAutoResume(paused, settings);
var roundTrip = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings));
var serializationWorks = roundTrip is { AutoResumeOnStart: true, Theme: "midnight", Icon: "dj-cat" };
var iconLevelsWork = AudioIconFrameSelector.Select(0, true, true) == 0 &&
    AudioIconFrameSelector.Select(.3f, true, true) == 1 &&
    AudioIconFrameSelector.Select(.8f, true, true) == 2 &&
    AudioIconFrameSelector.Select(.8f, false, true) == 0 &&
    AudioIconFrameSelector.Select(.8f, true, false) == 0 &&
    AudioIconFrameSelector.SelectAnimated(.3f, true, true, 0) == 0 &&
    AudioIconFrameSelector.SelectAnimated(.3f, true, true, 1) == 1 &&
    AudioIconFrameSelector.SelectAnimated(.8f, true, true, 0) == 1 &&
    AudioIconFrameSelector.SelectAnimated(.8f, true, true, 1) == 2;
var signal = new SignalGenerator(48_000, 2) { Frequency = 440, Gain = .7, Type = SignalGeneratorType.Sin };
var meter = new MeteringSampleProvider(signal, 6_000);
var measuredPeak = 0f;
meter.StreamVolume += (_, eventArgs) => measuredPeak = Math.Max(measuredPeak, eventArgs.MaxSampleValues.Max());
meter.Read(new float[24_000], 0, 24_000);
var audioMeterWorks = measuredPeak > .65f;
var passed = defaultsAreSafe && defaultDoesNotResume && optInResumesOnlyPlaying && serializationWorks && iconLevelsWork && audioMeterWorks;
Console.WriteLine(JsonSerializer.Serialize(new
{
    passed,
    defaultTheme = new AppSettings().Theme,
    defaultIcon = new AppSettings().Icon,
    animatedIconDefault = new AppSettings().AnimatedIconEnabled,
    audioIconLevelsWork = iconLevelsWork,
    audioMeterWorks,
    autoResumeDefault = new AppSettings().AutoResumeOnStart,
    optInResumesPlaying = optInResumesOnlyPlaying
}));
return passed ? 0 : 1;
