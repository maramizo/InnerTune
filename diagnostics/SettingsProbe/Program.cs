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
var iconAnimator = new AudioIconAnimator();
var animatedFrames = Enumerable.Range(0, 16).Select(_ => iconAnimator.Update(.8f, true, true)).ToArray();
var iconLevelsWork = iconAnimator.Update(0, false, true) == 0 &&
    animatedFrames.Distinct().Count() >= AudioIconAnimator.PhaseCount &&
    animatedFrames.Min() >= AudioIconAnimator.Encode(AudioIconAnimator.LevelCount - 1, 0) &&
    animatedFrames.Max() <= AudioIconAnimator.FrameCount - 1 &&
    AudioIconAnimator.Encode(AudioIconAnimator.LevelCount - 1, AudioIconAnimator.PhaseCount - 1) == AudioIconAnimator.FrameCount - 1;
static (double Bpm, int PhaseChanges) AnalyzeTempo(double bpm)
{
    var animator = new AudioIconAnimator();
    const double sampleSeconds = .125;
    var beatSeconds = 60 / bpm;
    var phaseChanges = 0;
    var previousPhase = -1;
    for (var elapsed = 0d; elapsed < 12; elapsed += sampleSeconds)
    {
        var beatPosition = elapsed % beatSeconds;
        var level = beatPosition < sampleSeconds ? .85f : .055f;
        var frame = animator.Update(level, true, true, sampleSeconds);
        var phase = frame <= 0 ? -1 : (frame - 1) % AudioIconAnimator.PhaseCount;
        if (elapsed >= 8 && previousPhase >= 0 && phase != previousPhase) phaseChanges++;
        previousPhase = phase;
    }
    return (animator.EstimatedBpm, phaseChanges);
}
var slowTempo = AnalyzeTempo(80);
var fastTempo = AnalyzeTempo(160);
var tempoTrackingWorks = Math.Abs(slowTempo.Bpm - 80) < 8 &&
    Math.Abs(fastTempo.Bpm - 160) < 12 &&
    fastTempo.Bpm > slowTempo.Bpm * 1.65 &&
    fastTempo.PhaseChanges > slowTempo.PhaseChanges * 1.25;
var signal = new SignalGenerator(48_000, 2) { Frequency = 440, Gain = .7, Type = SignalGeneratorType.Sin };
var meter = new MeteringSampleProvider(signal, 6_000);
var measuredPeak = 0f;
meter.StreamVolume += (_, eventArgs) => measuredPeak = Math.Max(measuredPeak, eventArgs.MaxSampleValues.Max());
meter.Read(new float[24_000], 0, 24_000);
var audioMeterWorks = measuredPeak > .65f;
var passed = defaultsAreSafe && defaultDoesNotResume && optInResumesOnlyPlaying && serializationWorks && iconLevelsWork && tempoTrackingWorks && audioMeterWorks;
Console.WriteLine(JsonSerializer.Serialize(new
{
    passed,
    defaultTheme = new AppSettings().Theme,
    defaultIcon = new AppSettings().Icon,
    animatedIconDefault = new AppSettings().AnimatedIconEnabled,
    audioIconLevelsWork = iconLevelsWork,
    tempoTrackingWorks,
    slowTempo = slowTempo.Bpm,
    fastTempo = fastTempo.Bpm,
    slowPhaseChanges = slowTempo.PhaseChanges,
    fastPhaseChanges = fastTempo.PhaseChanges,
    audioMeterWorks,
    autoResumeDefault = new AppSettings().AutoResumeOnStart,
    optInResumesPlaying = optInResumesOnlyPlaying
}));
return passed ? 0 : 1;
