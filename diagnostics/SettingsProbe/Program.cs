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
var passed = defaultsAreSafe && defaultDoesNotResume && optInResumesOnlyPlaying && serializationWorks && iconLevelsWork && tempoTrackingWorks && audioMeterWorks && pathMergeWorks && installerEnvironmentWorks && playbackStoreWorks;
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
    pathMergeWorks,
    installerEnvironmentWorks,
    playbackStoreWorks,
    autoResumeDefault = new AppSettings().AutoResumeOnStart,
    optInResumesPlaying = optInResumesOnlyPlaying
}));
return passed ? 0 : 1;
