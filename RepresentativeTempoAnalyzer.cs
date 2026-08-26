using NAudio.Dsp;
using NAudio.Wave;

namespace InnerTune;

public sealed record TempoAnalysis(
    double Bpm,
    TimeSpan SampleStart,
    double SampleLoudness,
    double AverageLoudness,
    double PeakLoudness,
    double FullnessFloor,
    double FullnessCeiling,
    List<JumpWindow> JumpWindows,
    double Danceability,
    DanceMetrics DanceMetrics);

public sealed record DanceMetrics(
    double Score,
    double Pulse,
    double OnsetPulse,
    double BroadOnsetPulse,
    double BassRhythm,
    double BassPresence,
    double SustainedEnergy,
    double Compression,
    double DenseRhythmicEnergy,
    double TransientStrength,
    double TempoFit);

public sealed record MotionPlanningInput(
    double Bpm,
    TimeSpan SampleStart,
    double SampleLoudness,
    double AverageLoudness,
    double PeakLoudness,
    double FullnessFloor,
    double FullnessCeiling,
    DanceMetrics DanceMetrics,
    List<float> Envelope,
    List<SpectralFrame> SpectralFrames);

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

    public static Task<MotionPlanningInput?> PrepareMotionPlanningAsync(string path,
        CancellationToken token = default) => Task.Run(() =>
    {
        var thread = Thread.CurrentThread;
        var previousPriority = thread.Priority;
        try
        {
            thread.Priority = ThreadPriority.BelowNormal;
            return PrepareMotionPlanning(path, token);
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
        var prepared = PrepareMotionPlanning(path, token);
        if (prepared is null) return null;
        var jumpWindows = JumpWindowPlanner.Plan(prepared.Envelope, EnvelopeSeconds,
            prepared.FullnessFloor, prepared.FullnessCeiling, prepared.DanceMetrics, prepared.SpectralFrames);
        return new TempoAnalysis(
            prepared.Bpm,
            prepared.SampleStart,
            prepared.SampleLoudness,
            prepared.AverageLoudness,
            prepared.PeakLoudness,
            prepared.FullnessFloor,
            prepared.FullnessCeiling,
            jumpWindows,
            prepared.DanceMetrics.Score,
            prepared.DanceMetrics);
    }

    private static MotionPlanningInput? PrepareMotionPlanning(string path, CancellationToken token)
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
        var sampledPeaks = new List<double>(centers.Length);
        var fullnessSamples = new List<float>();
        foreach (var center in centers)
        {
            var start = Math.Clamp(center - LoudnessWindowSeconds / 2, 0, Math.Max(0, duration - LoudnessWindowSeconds));
            reader.CurrentTime = TimeSpan.FromSeconds(start);
            var features = ReadEnvelope(samples, reader.WaveFormat, LoudnessWindowSeconds, token);
            fullnessSamples.AddRange(features.Full);
            loudness.Add(features.Full.Count == 0 ? 0 : features.Full.Average(value => (double)value));
            sampledPeaks.Add(features.Full.Count == 0 ? 0 : features.Full.Max());
        }

        var selected = SelectRepresentativeWindow(loudness);
        if (selected < 0) return null;
        var sampleDuration = Math.Min(TempoWindowSeconds, duration);
        var sampleStart = Math.Clamp(centers[selected] - sampleDuration / 2, 0, Math.Max(0, duration - sampleDuration));
        reader.CurrentTime = TimeSpan.FromSeconds(sampleStart);
        var tempoFeatures = ReadEnvelope(samples, reader.WaveFormat, sampleDuration, token);
        var tracker = new BeatTempoTracker();
        foreach (var level in tempoFeatures.Full)
        {
            tracker.Update(level, EnvelopeSeconds);
            if (tracker.IsLocked) break;
        }
        if (!tracker.HasEstimate) return null;
        var danceMetrics = MeasureDanceability(tempoFeatures.Full, tempoFeatures.Low, tracker.Bpm);
        reader.CurrentTime = TimeSpan.Zero;
        var motionFeatures = ReadEnvelope(samples, reader.WaveFormat, duration, token, true);
        fullnessSamples.AddRange(motionFeatures.Full);
        var fullnessFloor = Percentile(fullnessSamples, .20);
        var fullnessCeiling = Percentile(fullnessSamples, .95);
        if (fullnessCeiling - fullnessFloor < .025)
            fullnessCeiling = Math.Min(1, fullnessFloor + .025);
        var peakLoudness = Math.Max(
            sampledPeaks.Count == 0 ? 0 : sampledPeaks.Max(),
            tempoFeatures.Full.Count == 0 ? 0 : tempoFeatures.Full.Max());
        return new MotionPlanningInput(
            tracker.Bpm,
            TimeSpan.FromSeconds(sampleStart),
            loudness[selected],
            loudness.Average(),
            peakLoudness,
            fullnessFloor,
            fullnessCeiling,
            danceMetrics,
            motionFeatures.Full,
            motionFeatures.Spectral);
    }

    public static double EstimateDanceability(
        IReadOnlyList<float> fullEnvelope,
        IReadOnlyList<float> lowEnvelope,
        double bpm) => MeasureDanceability(fullEnvelope, lowEnvelope, bpm).Score;

    public static DanceMetrics MeasureDanceability(
        IReadOnlyList<float> fullEnvelope,
        IReadOnlyList<float> lowEnvelope,
        double bpm)
    {
        var count = Math.Min(fullEnvelope.Count, lowEnvelope.Count);
        if (count < 16) return new DanceMetrics(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var full = fullEnvelope.Take(count).Select(value => (double)value).ToArray();
        var low = lowEnvelope.Take(count).Select(value => (double)value).ToArray();
        var mean = Math.Max(.0001, full.Average());
        var rawPulse = Periodicity(full, bpm);
        var fullOnsets = OnsetEnvelope(full);
        var lowOnsets = OnsetEnvelope(low);
        var onsetPulse = Periodicity(fullOnsets, bpm);
        var broadOnsetPulse = RhythmicPeriodicity(fullOnsets);
        var pulse = Math.Max(rawPulse, Math.Max(onsetPulse, broadOnsetPulse));
        var lowPulse = Periodicity(low, bpm);
        var lowOnsetPulse = Math.Max(Periodicity(lowOnsets, bpm), RhythmicPeriodicity(lowOnsets));
        var bassShare = Math.Clamp(low.Average() / mean, 0, 1);
        var bassPresence = SmoothStep(.12, .46, bassShare);
        var sustainedEnergy = SmoothStep(.10, .28, mean);
        var compression = SmoothStep(.46, .76, mean / Math.Max(mean, full.Max()));
        var positiveFlux = Enumerable.Range(1, count - 1)
            .Average(index => Math.Max(0, full[index] - full[index - 1])) / mean;
        var transientStrength = SmoothStep(.035, .28, positiveFlux);
        var bassRhythm = Math.Max(lowPulse, lowOnsetPulse) * bassPresence;
        var tempoFit = SmoothStep(72, 96, bpm) * (1 - SmoothStep(178, 194, bpm));
        var rhythmicScore = .42 * pulse + .25 * bassRhythm + .21 * transientStrength + .12 * tempoFit;
        var rhythmicSupport = Math.Max(pulse, Math.Max(bassRhythm, transientStrength));
        // Dense rock often has a nearly flat RMS envelope because the master is
        // continuously loud. Its beat disappears from coarse autocorrelation,
        // so combine sustained energy, compression and bass presence with the
        // rhythmic evidence instead of treating the flat envelope as calm.
        var denseRhythmicEnergy = sustainedEnergy * compression * bassPresence * tempoFit *
            (.65 + .35 * rhythmicSupport);
        var score = Math.Clamp(Math.Max(rhythmicScore, denseRhythmicEnergy), 0, 1);
        return new DanceMetrics(score, rawPulse, onsetPulse, broadOnsetPulse, bassRhythm, bassPresence,
            sustainedEnergy, compression, denseRhythmicEnergy, transientStrength, tempoFit);
    }

    private static double[] OnsetEnvelope(IReadOnlyList<double> values)
    {
        var onsets = new double[values.Count];
        for (var index = 1; index < values.Count; index++)
            onsets[index] = Math.Max(0, values[index] - values[index - 1]);
        return onsets;
    }

    private static double RhythmicPeriodicity(IReadOnlyList<double> values) =>
        Enumerable.Range(3, 5)
            .Select(lag => Math.Max(0, Correlation(values, lag)))
            .DefaultIfEmpty(0)
            .Max();

    private static double Periodicity(IReadOnlyList<double> values, double bpm)
    {
        var expectedLag = 60 / Math.Clamp(bpm, BeatTempoTracker.MinimumBpm, BeatTempoTracker.MaximumBpm) / EnvelopeSeconds;
        var candidates = new[]
        {
            (int)Math.Round(expectedLag) - 1,
            (int)Math.Round(expectedLag),
            (int)Math.Round(expectedLag) + 1,
            (int)Math.Round(expectedLag * 2)
        };
        return candidates.Where(lag => lag is >= 2 && lag < values.Count / 2)
            .Select(lag => Math.Max(0, Correlation(values, lag)))
            .DefaultIfEmpty(0)
            .Max();
    }

    private static double Correlation(IReadOnlyList<double> values, int lag)
    {
        var pairs = values.Count - lag;
        if (pairs < 8) return 0;
        var leftMean = Enumerable.Range(0, pairs).Average(index => values[index]);
        var rightMean = Enumerable.Range(0, pairs).Average(index => values[index + lag]);
        double product = 0, leftSquares = 0, rightSquares = 0;
        for (var index = 0; index < pairs; index++)
        {
            var left = values[index] - leftMean;
            var right = values[index + lag] - rightMean;
            product += left * right;
            leftSquares += left * left;
            rightSquares += right * right;
        }
        var denominator = Math.Sqrt(leftSquares * rightSquares);
        return denominator <= 1e-12 ? 0 : product / denominator;
    }

    private static double SmoothStep(double low, double high, double value)
    {
        var position = Math.Clamp((value - low) / (high - low), 0, 1);
        return position * position * (3 - 2 * position);
    }

    private static double Percentile(IReadOnlyList<float> values, double percentile)
    {
        if (values.Count == 0) return 0;
        var sorted = values.Select(value => (double)value).OrderBy(value => value).ToArray();
        var position = Math.Clamp(percentile, 0, 1) * (sorted.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = Math.Min(sorted.Length - 1, lower + 1);
        var fraction = position - lower;
        return sorted[lower] + (sorted[upper] - sorted[lower]) * fraction;
    }

    private sealed record EnvelopeFeatures(List<float> Full, List<float> Low, List<SpectralFrame> Spectral);

    private static EnvelopeFeatures ReadEnvelope(ISampleProvider samples, WaveFormat format, double seconds,
        CancellationToken token, bool captureSpectral = false)
    {
        var samplesPerBucket = Math.Max(format.Channels, (int)Math.Round(format.SampleRate * format.Channels * EnvelopeSeconds));
        var requestedBuckets = Math.Max(1, (int)Math.Ceiling(seconds / EnvelopeSeconds));
        var buffer = new float[Math.Min(samplesPerBucket, 16_384)];
        var fullEnvelope = new List<float>(requestedBuckets);
        var lowEnvelope = new List<float>(requestedBuckets);
        var lowState = new double[Math.Max(1, format.Channels)];
        var spectral = captureSpectral ? new SpectralAccumulator(format.SampleRate) : null;
        var lowPassAlpha = 1 - Math.Exp(-2 * Math.PI * 180 / format.SampleRate);
        double squareSum = 0, lowSquareSum = 0;
        var bucketSamples = 0;
        while (fullEnvelope.Count < requestedBuckets)
        {
            token.ThrowIfCancellationRequested();
            var read = samples.Read(buffer, 0, Math.Min(buffer.Length, samplesPerBucket - bucketSamples));
            if (read <= 0) break;
            for (var index = 0; index < read; index++)
            {
                var sample = buffer[index];
                var channel = (bucketSamples + index) % lowState.Length;
                if (channel == 0) spectral?.Add(sample);
                lowState[channel] += lowPassAlpha * (sample - lowState[channel]);
                squareSum += sample * sample;
                lowSquareSum += lowState[channel] * lowState[channel];
            }
            bucketSamples += read;
            if (bucketSamples < samplesPerBucket) continue;
            fullEnvelope.Add((float)Math.Sqrt(squareSum / bucketSamples));
            lowEnvelope.Add((float)Math.Sqrt(lowSquareSum / bucketSamples));
            squareSum = 0;
            lowSquareSum = 0;
            bucketSamples = 0;
        }
        if (bucketSamples > 0 && fullEnvelope.Count < requestedBuckets)
        {
            fullEnvelope.Add((float)Math.Sqrt(squareSum / bucketSamples));
            lowEnvelope.Add((float)Math.Sqrt(lowSquareSum / bucketSamples));
        }
        spectral?.Complete();
        return new EnvelopeFeatures(fullEnvelope, lowEnvelope, spectral?.Frames ?? []);
    }

    private sealed class SpectralAccumulator
    {
        private const int FftSize = 2048;
        private const int FftPower = 11;
        private const int BandCount = 13;
        private readonly int _sampleRate;
        private readonly int _decimation;
        private readonly double _effectiveSampleRate;
        private readonly Complex[] _fft = new Complex[FftSize];
        private readonly double[] _bandTotals = new double[BandCount];
        private readonly (int Start, int End)[] _bands = new (int, int)[BandCount];
        private int _monoSamples;
        private int _fftSamples;
        private int _fftCount;

        public List<SpectralFrame> Frames { get; } = [];

        public SpectralAccumulator(int sampleRate)
        {
            _sampleRate = sampleRate;
            _decimation = Math.Max(1, (int)Math.Round(sampleRate / 11_025d));
            _effectiveSampleRate = sampleRate / (double)_decimation;
            var edges = Enumerable.Range(0, BandCount + 1)
                .Select(index => 55 * Math.Pow(5000d / 55, index / (double)BandCount))
                .ToArray();
            for (var band = 0; band < BandCount; band++)
                _bands[band] = (
                    Math.Max(1, (int)Math.Ceiling(edges[band] * FftSize / _effectiveSampleRate)),
                    Math.Min(FftSize / 2, (int)Math.Floor(edges[band + 1] * FftSize / _effectiveSampleRate)));
        }

        public void Add(float sample)
        {
            if (_monoSamples % _decimation == 0)
            {
                var window = .5 - .5 * Math.Cos(2 * Math.PI * _fftSamples / (FftSize - 1));
                _fft[_fftSamples++] = new Complex { X = (float)(sample * window) };
                if (_fftSamples == FftSize) ProcessFft();
            }
            _monoSamples++;
            if (_monoSamples % _sampleRate == 0) EmitFrame();
        }

        public void Complete()
        {
            if (_monoSamples % _sampleRate != 0) EmitFrame();
        }

        private void ProcessFft()
        {
            while (_fftSamples < FftSize) _fft[_fftSamples++] = default;
            FastFourierTransform.FFT(true, FftPower, _fft);
            for (var band = 0; band < BandCount; band++)
            {
                var (start, end) = _bands[band];
                double total = 0;
                for (var bin = start; bin <= end; bin++)
                    total += _fft[bin].X * _fft[bin].X + _fft[bin].Y * _fft[bin].Y;
                _bandTotals[band] += Math.Log(total / Math.Max(1, end - start + 1) + 1e-12);
            }
            _fftCount++;
            Array.Clear(_fft);
            _fftSamples = 0;
        }

        private void EmitFrame()
        {
            if (_fftSamples > 0) ProcessFft();
            if (_fftCount == 0) return;
            Frames.Add(new SpectralFrame(_bandTotals.Select(total => total / _fftCount).ToArray()));
            Array.Clear(_bandTotals);
            _fftCount = 0;
        }
    }
}
