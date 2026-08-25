using System.Text.Json;
using InnerTune;
using NAudio.Wave;
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
iconAnimator.SetTempo(100);
var animatedFrames = Enumerable.Range(0, 72).Select(_ => iconAnimator.Update(.8f, true, true, 1d / 30)).ToArray();
var iconLevelsWork = iconAnimator.Update(0, false, true) == 0 &&
    animatedFrames.Distinct().Count() >= AudioIconAnimator.PhaseCount &&
    animatedFrames.Min() >= AudioIconAnimator.Encode(AudioIconAnimator.LevelCount - 1, 0) &&
    animatedFrames.Max() <= AudioIconAnimator.FrameCount - 1 &&
    AudioIconAnimator.EncodeJump(AudioIconAnimator.PhaseCount - 1) == AudioIconAnimator.FrameCount - 1;
var latchingAnimator = new AudioIconAnimator();
latchingAnimator.SetTempo(120);
latchingAnimator.SetMotionProfile(.92, .78);
var priorLevel = 0;
var priorJump = false;
var sawLevelChange = false;
var sawJump = false;
var motionChangesOnlyAtRest = true;
for (var sample = 0; sample < 360; sample++)
{
    var level = sample % 48 < 24 ? .95f : .03f;
    var frame = latchingAnimator.Update(level, true, true, 1d / 30);
    var frameLevel = AudioIconAnimator.DecodeLevel(frame);
    var jumping = AudioIconAnimator.IsJumpFrame(frame);
    if (frameLevel != priorLevel || jumping != priorJump)
    {
        sawLevelChange |= frameLevel != priorLevel;
        sawJump |= jumping;
        motionChangesOnlyAtRest &= AudioIconAnimator.IsRestPhase(AudioIconAnimator.DecodePhase(frame));
    }
    priorLevel = frameLevel;
    priorJump = jumping;
}
var groundedMotionTransitionsWork = sawLevelChange && sawJump && motionChangesOnlyAtRest;
var offBeatJumpAnimator = new AudioIconAnimator();
offBeatJumpAnimator.SetTempo(120);
offBeatJumpAnimator.SetMotionProfile(1, .35);
var offBeatJumpFrame = 0;
for (var sample = 0; sample <= 15; sample++)
    offBeatJumpFrame = offBeatJumpAnimator.Update(sample == 15 ? .001f : .95f, true, true, 1d / 30);
var offBeatLandingStillJumps = AudioIconAnimator.IsJumpFrame(offBeatJumpFrame) &&
    AudioIconAnimator.IsRestPhase(AudioIconAnimator.DecodePhase(offBeatJumpFrame));
var pianoAnimator = new AudioIconAnimator();
pianoAnimator.SetTempo(120);
pianoAnimator.SetMotionProfile(.18, .22);
var pianoDoesNotJump = Enumerable.Range(0, 360)
    .Select(index => pianoAnimator.Update(index % 30 == 0 ? .9f : .15f, true, true, 1d / 30))
    .All(frame => !AudioIconAnimator.IsJumpFrame(frame));
var danceEnvelope = Enumerable.Range(0, 96).Select(index => index % 4 == 0 ? .74f : .10f).ToArray();
var danceLowEnvelope = Enumerable.Range(0, 96).Select(index => index % 4 == 0 ? .40f : .035f).ToArray();
var pianoEnvelope = Enumerable.Range(0, 96).Select(index => .18f + .05f * (float)Math.Sin(index * .37)).ToArray();
var pianoLowEnvelope = pianoEnvelope.Select(value => value * .18f).ToArray();
var danceScore = RepresentativeTempoAnalyzer.EstimateDanceability(danceEnvelope, danceLowEnvelope, 120);
var pianoScore = RepresentativeTempoAnalyzer.EstimateDanceability(pianoEnvelope, pianoLowEnvelope, 120);
var raveClassificationWorks = danceScore >= .58 && pianoScore <= .45 && danceScore > pianoScore + .25;
static (double Bpm, int PhaseChanges, bool Locked, bool Stable) AnalyzeTempo(double bpm)
{
    var tracker = new BeatTempoTracker();
    const double sampleSeconds = .125;
    var beatSeconds = 60 / bpm;
    for (var elapsed = 0d; elapsed < 12; elapsed += sampleSeconds)
    {
        var beatPosition = elapsed % beatSeconds;
        var level = beatPosition < sampleSeconds ? .85f : .055f;
        tracker.Update(level, sampleSeconds);
    }
    var lockedBpm = tracker.Bpm;
    for (var elapsed = 0d; elapsed < 8; elapsed += sampleSeconds)
    {
        var beatPosition = elapsed % (60 / 137d);
        var level = beatPosition < sampleSeconds ? .9f : .04f;
        tracker.Update(level, sampleSeconds);
    }
    var animator = new AudioIconAnimator();
    animator.SetTempo(lockedBpm);
    var phaseChanges = 0;
    var previousPhase = -1;
    for (var elapsed = 0d; elapsed < 4; elapsed += 1d / 30)
    {
        var frame = animator.Update(.8f, true, true, 1d / 30);
        var phase = (frame - 1) % AudioIconAnimator.PhaseCount;
        if (previousPhase >= 0 && phase != previousPhase) phaseChanges++;
        previousPhase = phase;
    }
    return (lockedBpm, phaseChanges, tracker.HasEstimate, Math.Abs(tracker.Bpm - lockedBpm) < .001);
}
var slowTempo = AnalyzeTempo(80);
var fastTempo = AnalyzeTempo(160);
var tempoTrackingWorks = Math.Abs(slowTempo.Bpm - 80) < 8 &&
    Math.Abs(fastTempo.Bpm - 160) < 12 &&
    fastTempo.Bpm > slowTempo.Bpm * 1.65 &&
    fastTempo.PhaseChanges > slowTempo.PhaseChanges * 1.25 &&
    slowTempo.Locked && fastTempo.Locked && slowTempo.Stable && fastTempo.Stable;
var representativeWindowWorks = RepresentativeTempoAnalyzer.SelectRepresentativeWindow([.10, .45, .52, .93]) == 2;
var representativeAudioSampleWorks = false;
var representativeSampleStart = -1d;
var representativeSampleBpm = -1d;
var tempoProbeRoot = Path.Combine(Path.GetTempPath(), $"InnerTuneTempoProbe-{Guid.NewGuid():N}");
try
{
    Directory.CreateDirectory(tempoProbeRoot);
    var path = Path.Combine(tempoProbeRoot, "representative.wav");
    const int sampleRate = 16_000;
    const int durationSeconds = 60;
    double[] segmentLevels = [.10, .30, .50, .70, .90];
    using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1)))
    {
        for (var sample = 0; sample < sampleRate * durationSeconds; sample++)
        {
            var seconds = sample / (double)sampleRate;
            var segment = Math.Min(segmentLevels.Length - 1, (int)(seconds / 12));
            var beat = seconds % .5 < .08 ? 1 : .02;
            writer.WriteSample((float)(segmentLevels[segment] * beat * Math.Sin(seconds * Math.PI * 440)));
        }
    }
    var analysis = await RepresentativeTempoAnalyzer.AnalyzeAsync(path);
    representativeSampleStart = analysis?.SampleStart.TotalSeconds ?? -1;
    representativeSampleBpm = analysis?.Bpm ?? -1;
    representativeAudioSampleWorks = analysis is not null &&
        Math.Abs(analysis.Bpm - 120) < 12 &&
        analysis.SampleStart.TotalSeconds is >= 23 and <= 25;
}
finally
{
    if (Directory.Exists(tempoProbeRoot)) Directory.Delete(tempoProbeRoot, true);
}
var signal = new SignalGenerator(48_000, 2) { Frequency = 440, Gain = .7, Type = SignalGeneratorType.Sin };
var meter = new RmsMeteringSampleProvider(signal, 6_000);
var measuredLoudness = 0f;
meter.LoudnessAvailable += value => measuredLoudness = Math.Max(measuredLoudness, value);
meter.Read(new float[24_000], 0, 24_000);
var audioMeterWorks = measuredLoudness is > .48f and < .51f;
var effectivePath = WindowsPathEnvironment.BuildEffectivePath(
    @"C:\Machine;%SystemRoot%\System32",
    @"C:\User;%PATH%",
    @"C:\Transient;C:\Machine");
var effectivePathParts = effectivePath.Split(Path.PathSeparator);
var pathMergeWorks = effectivePathParts.Contains(@"C:\Machine", StringComparer.OrdinalIgnoreCase) &&
    effectivePathParts.Contains(@"C:\User", StringComparer.OrdinalIgnoreCase) &&
    effectivePathParts.Contains(@"C:\Transient", StringComparer.OrdinalIgnoreCase) &&
    effectivePathParts.Count(path => path.Equals(@"C:\Machine", StringComparison.OrdinalIgnoreCase)) == 1 &&
    !effectivePath.Contains("%PATH%", StringComparison.OrdinalIgnoreCase);
var installerEnvironmentWorks = false;
var installerProbeRoot = Path.Combine(Path.GetTempPath(), $"InnerTuneInstallerEnvironment-{Guid.NewGuid():N}");
try
{
    Directory.CreateDirectory(installerProbeRoot);
    var installerPath = Path.Combine(installerProbeRoot, "update.exe");
    await File.WriteAllBytesAsync(installerPath, [0]);
    var update = new AppUpdate(new Version(9, 9, 9), "v9.9.9", "https://example.invalid/update", "https://example.invalid/checksum", "https://example.invalid/release", "", installerPath);
    var start = UpdateService.CreateInstallerStartInfo(update);
    installerEnvironmentWorks = !start.UseShellExecute &&
        start.WorkingDirectory == installerProbeRoot &&
        start.Environment.TryGetValue("PATH", out var installerPathValue) &&
        installerPathValue == WindowsPathEnvironment.BuildEffectivePath(
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH"));
}
finally
{
    if (Directory.Exists(installerProbeRoot)) Directory.Delete(installerProbeRoot, true);
}
var playbackRoot = Path.Combine(Path.GetTempPath(), $"InnerTunePlaybackStore-{Guid.NewGuid():N}");
var playbackStoreWorks = false;
try
{
    var store = new LibraryStore(playbackRoot);
    var library = new LibraryData
    {
        Queue = [new Track { Id = "track", Title = "Track", Artist = "Artist" }],
        Playback = new PlaybackSnapshot { Status = "playing", TrackId = "track", PositionSeconds = 12 }
    };
    await store.SaveAsync(library);
    var libraryBefore = await File.ReadAllBytesAsync(store.FilePath);
    var unchangedWriteTime = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    File.SetLastWriteTimeUtc(store.FilePath, unchangedWriteTime);
    library.Playback.PositionSeconds = 47;
    await store.SavePlaybackAsync(library.Playback);
    var libraryAfter = await File.ReadAllBytesAsync(store.FilePath);
    var reloaded = await store.LoadAsync();
    playbackStoreWorks = libraryBefore.SequenceEqual(libraryAfter) &&
        File.GetLastWriteTimeUtc(store.FilePath) == unchangedWriteTime &&
        File.Exists(store.PlaybackFilePath) &&
        reloaded.Playback.PositionSeconds == 47;
}
finally
{
    if (Directory.Exists(playbackRoot)) Directory.Delete(playbackRoot, true);
}
TempoAnalysis? inspectedAnalysis = null;
double? inspectedJumpCoverage = null;
if (args.FirstOrDefault() is { Length: > 0 } inspectedPath && File.Exists(inspectedPath))
{
    inspectedAnalysis = await RepresentativeTempoAnalyzer.AnalyzeAsync(inspectedPath);
    if (inspectedAnalysis is not null)
    {
        using var inspectedReader = new MediaFoundationReader(inspectedPath);
        var inspectedMeter = new RmsMeteringSampleProvider(
            inspectedReader.ToSampleProvider(),
            inspectedReader.WaveFormat.SampleRate * inspectedReader.WaveFormat.Channels / 30);
        var inspectedAnimator = new AudioIconAnimator();
        inspectedAnimator.SetTempo(inspectedAnalysis.Bpm);
        inspectedAnimator.SetMotionProfile(inspectedAnalysis.Danceability, inspectedAnalysis.PeakLoudness);
        var inspectedFrames = 0;
        var inspectedJumpFrames = 0;
        inspectedMeter.LoudnessAvailable += loudness =>
        {
            var frame = inspectedAnimator.Update(loudness, true, true, 1d / 30);
            inspectedFrames++;
            if (AudioIconAnimator.IsJumpFrame(frame)) inspectedJumpFrames++;
        };
        var inspectedBuffer = new float[32_768];
        while (inspectedMeter.Read(inspectedBuffer, 0, inspectedBuffer.Length) > 0) { }
        inspectedJumpCoverage = inspectedFrames == 0 ? 0 : inspectedJumpFrames / (double)inspectedFrames;
    }
}
var passed = defaultsAreSafe && defaultDoesNotResume && optInResumesOnlyPlaying && serializationWorks && iconLevelsWork && groundedMotionTransitionsWork && offBeatLandingStillJumps && pianoDoesNotJump && raveClassificationWorks && tempoTrackingWorks && representativeWindowWorks && representativeAudioSampleWorks && audioMeterWorks && pathMergeWorks && installerEnvironmentWorks && playbackStoreWorks;
Console.WriteLine(JsonSerializer.Serialize(new
{
    passed,
    defaultTheme = new AppSettings().Theme,
    defaultIcon = new AppSettings().Icon,
    animatedIconDefault = new AppSettings().AnimatedIconEnabled,
    audioIconLevelsWork = iconLevelsWork,
    groundedMotionTransitionsWork,
    offBeatLandingStillJumps,
    pianoDoesNotJump,
    raveClassificationWorks,
    danceScore,
    pianoScore,
    tempoTrackingWorks,
    slowTempo = slowTempo.Bpm,
    fastTempo = fastTempo.Bpm,
    slowPhaseChanges = slowTempo.PhaseChanges,
    fastPhaseChanges = fastTempo.PhaseChanges,
    tempoLocksAfterSample = slowTempo.Locked && fastTempo.Locked,
    tempoRemainsStable = slowTempo.Stable && fastTempo.Stable,
    representativeWindowWorks,
    representativeAudioSampleWorks,
    representativeSampleStart,
    representativeSampleBpm,
    audioMeterWorks,
    measuredLoudness,
    pathMergeWorks,
    installerEnvironmentWorks,
    playbackStoreWorks,
    inspectedAnalysis,
    inspectedJumpCoverage,
    autoResumeDefault = new AppSettings().AutoResumeOnStart,
    optInResumesPlaying = optInResumesOnlyPlaying
}));
return passed ? 0 : 1;
