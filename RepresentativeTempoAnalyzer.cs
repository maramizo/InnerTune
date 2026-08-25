using NAudio.Wave;

namespace InnerTune;

public sealed record TempoAnalysis(double Bpm, TimeSpan SampleStart, double SampleLoudness, double AverageLoudness);

public static class RepresentativeTempoAnalyzer
{
    private const double EnvelopeSeconds = .125;
    private const double LoudnessWindowSeconds = 2;
    private const double TempoWindowSeconds = BeatTempoTracker.MaximumSampleSeconds;
    private static readonly double[] CandidateFractions = [.18, .34, .50, .66, .82];

    public static Task<TempoAnalysis?> AnalyzeAsync(string path, CancellationToken token = default) => Task.Run(() =>
    {
        var thread = Thread.CurrentThread;
        var previousPriority = thread.Priority;
        try
        {
            thread.Priority = ThreadPriority.BelowNormal;
            return Analyze(path, token);
        }
        finally
        {
            try { thread.Priority = previousPriority; } catch { }
        }
    }, token);

    public static int SelectRepresentativeWindow(IReadOnlyList<double> loudness)
    {
        if (loudness.Count == 0) return -1;
        var average = loudness.Average();
        return Enumerable.Range(0, loudness.Count)
            .MinBy(index => Math.Abs(loudness[index] - average));
    }

    private static TempoAnalysis? Analyze(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var reader = new MediaFoundationReader(path);
        var duration = reader.TotalTime.TotalSeconds;
        if (duration <= 0) return null;
        var samples = reader.ToSampleProvider();
        var centers = CandidateFractions
            .Select(fraction => duration * fraction)
            .Distinct()
            .ToArray();
        var loudness = new List<double>(centers.Length);
        foreach (var center in centers)
        {
            var start = Math.Clamp(center - LoudnessWindowSeconds / 2, 0, Math.Max(0, duration - LoudnessWindowSeconds));
            reader.CurrentTime = TimeSpan.FromSeconds(start);
            var envelope = ReadEnvelope(samples, reader.WaveFormat, LoudnessWindowSeconds, token);
            loudness.Add(envelope.Count == 0 ? 0 : envelope.Average(value => (double)value));
        }

        var selected = SelectRepresentativeWindow(loudness);
        if (selected < 0) return null;
        var sampleDuration = Math.Min(TempoWindowSeconds, duration);
        var sampleStart = Math.Clamp(centers[selected] - sampleDuration / 2, 0, Math.Max(0, duration - sampleDuration));
        reader.CurrentTime = TimeSpan.FromSeconds(sampleStart);
        var tempoEnvelope = ReadEnvelope(samples, reader.WaveFormat, sampleDuration, token);
        var tracker = new BeatTempoTracker();
        foreach (var level in tempoEnvelope)
        {
            tracker.Update(level, EnvelopeSeconds);
            if (tracker.IsLocked) break;
        }
        if (!tracker.HasEstimate) return null;
        return new TempoAnalysis(tracker.Bpm, TimeSpan.FromSeconds(sampleStart), loudness[selected], loudness.Average());
    }

    private static List<float> ReadEnvelope(ISampleProvider samples, WaveFormat format, double seconds, CancellationToken token)
    {
        var samplesPerBucket = Math.Max(format.Channels, (int)Math.Round(format.SampleRate * format.Channels * EnvelopeSeconds));
        var requestedBuckets = Math.Max(1, (int)Math.Ceiling(seconds / EnvelopeSeconds));
        var buffer = new float[Math.Min(samplesPerBucket, 16_384)];
        var envelope = new List<float>(requestedBuckets);
        double squareSum = 0;
        var bucketSamples = 0;
        while (envelope.Count < requestedBuckets)
        {
            token.ThrowIfCancellationRequested();
            var read = samples.Read(buffer, 0, Math.Min(buffer.Length, samplesPerBucket - bucketSamples));
            if (read <= 0) break;
            for (var index = 0; index < read; index++) squareSum += buffer[index] * buffer[index];
            bucketSamples += read;
            if (bucketSamples < samplesPerBucket) continue;
            envelope.Add((float)Math.Sqrt(squareSum / bucketSamples));
            squareSum = 0;
            bucketSamples = 0;
        }
        if (bucketSamples > 0 && envelope.Count < requestedBuckets)
            envelope.Add((float)Math.Sqrt(squareSum / bucketSamples));
        return envelope;
    }
}
