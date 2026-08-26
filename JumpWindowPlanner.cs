namespace InnerTune;

public sealed record JumpWindow(double StartSeconds, double EndSeconds)
{
    public double DurationSeconds => Math.Max(0, EndSeconds - StartSeconds);
    public bool Contains(double positionSeconds) => positionSeconds >= StartSeconds && positionSeconds < EndSeconds;
}

public sealed record SpectralFrame(double[] Bands);

public sealed record ChorusDetectionParameters(
    double PeakTargetBase = .147211851478232,
    double PeakTargetScale = .2572716941288558,
    double PeakTargetExponent = 4.910400560872574,
    double MinimumPeakAverage = .3,
    double RepeatedSimilarityThreshold = .5813331081675124,
    double MinimumStrongestSimilarity = .7216834993052672,
    double SimilaritySlack = .045987742464836064,
    double TransitionThreshold = .5038256935271165,
    double SupportTolerance = .0629174882472001,
    double BackwardSupportGain = .13943904475181862,
    int MaximumRepeatedRunSeconds = 24)
{
    public static ChorusDetectionParameters Default { get; } = new();
}

public static class JumpWindowPlanner
{
    public const double MinimumJumpSeconds = 5;
    private const double MaximumBeatScaleGapSeconds = 1.25;

    public static List<JumpWindow> Plan(
        IReadOnlyList<float> envelope,
        double sampleSeconds,
        double fullnessFloor,
        double fullnessCeiling,
        DanceMetrics danceMetrics,
        IReadOnlyList<SpectralFrame>? spectralFrames = null,
        ChorusDetectionParameters? parameters = null)
    {
        parameters ??= ChorusDetectionParameters.Default;
        if (envelope.Count == 0 || sampleSeconds <= 0 || !IsJumpWorthy(danceMetrics)) return [];
        var minimumBuckets = Math.Max(1, (int)Math.Ceiling(MinimumJumpSeconds / sampleSeconds));
        if (envelope.Count < minimumBuckets) return [];

        var range = Math.Max(.025, fullnessCeiling - fullnessFloor);
        var sustained = 0d;
        var normalized = new double[envelope.Count];
        for (var index = 0; index < envelope.Count; index++)
        {
            var input = Math.Clamp(envelope[index], 0, 1);
            var baselineTicks = sampleSeconds * 15;
            var blend = 1 - Math.Pow(1 - (input > sustained ? .18 : .08), baselineTicks);
            sustained += (input - sustained) * blend;
            normalized[index] = Math.Clamp((sustained - fullnessFloor) / range, 0, 1);
        }

        var rollingAverages = new double[normalized.Length - minimumBuckets + 1];
        var rollingTotal = normalized.Take(minimumBuckets).Sum();
        for (var index = 0; index < rollingAverages.Length; index++)
        {
            if (index > 0)
            {
                rollingTotal -= normalized[index - 1];
                rollingTotal += normalized[index + minimumBuckets - 1];
            }
            rollingAverages[index] = rollingTotal / minimumBuckets;
        }

        // Stronger, rhythmically fuller tracks may spend more time airborne;
        // quieter rock only selects its most prominent chorus-sized regions.
        var targetFraction = parameters.PeakTargetBase + parameters.PeakTargetScale *
            Math.Pow(Math.Clamp(danceMetrics.Score, 0, 1), parameters.PeakTargetExponent);
        var peakAverage = Math.Max(parameters.MinimumPeakAverage,
            Percentile(rollingAverages, 1 - Math.Clamp(targetFraction, .01, .80)));
        var candidates = new List<JumpWindow>();
        for (var index = 0; index < rollingAverages.Length; index++)
        {
            if (rollingAverages[index] < peakAverage) continue;

            candidates.Add(new JumpWindow(
                index * sampleSeconds,
                Math.Min(envelope.Count * sampleSeconds, (index + minimumBuckets) * sampleSeconds)));
        }
        if (candidates.Count == 0) return [];

        var merged = new List<JumpWindow>();
        var current = candidates[0];
        foreach (var candidate in candidates.Skip(1))
        {
            if (candidate.StartSeconds - current.EndSeconds <= MaximumBeatScaleGapSeconds)
                current = current with { EndSeconds = candidate.EndSeconds };
            else
            {
                merged.Add(current);
                current = candidate;
            }
        }
        merged.Add(current);
        var energyPeaks = merged.Where(window => window.DurationSeconds >= MinimumJumpSeconds).ToList();
        return spectralFrames is { Count: >= 40 }
            ? RefineToRepeatedSections(energyPeaks, spectralFrames, normalized, sampleSeconds, parameters)
            : energyPeaks;
    }

    public static bool IsJumpWorthy(DanceMetrics metrics)
    {
        var rhythmicPulse = Math.Max(metrics.Pulse, Math.Max(metrics.OnsetPulse, metrics.BroadOnsetPulse));
        var denseRock = metrics.DenseRhythmicEnergy >= .45;
        var bassDriven = metrics.SustainedEnergy >= .65 && rhythmicPulse >= .40 && metrics.BassRhythm >= .32;
        var percussive = metrics.OnsetPulse >= .20 && rhythmicPulse >= .28 && metrics.TransientStrength >= .18;
        return denseRock || bassDriven || percussive;
    }

    private static List<JumpWindow> RefineToRepeatedSections(
        IReadOnlyList<JumpWindow> energyPeaks,
        IReadOnlyList<SpectralFrame> spectralFrames,
        IReadOnlyList<double> normalizedFullness,
        double sampleSeconds,
        ChorusDetectionParameters parameters)
    {
        if (energyPeaks.Count == 0) return [];
        var bandCount = spectralFrames.Min(frame => frame.Bands.Length);
        if (bandCount == 0) return energyPeaks.ToList();

        var features = NormalizeFeatures(spectralFrames, bandCount);
        var repeatedRuns = FindRepeatedRuns(features, parameters);
        if (repeatedRuns.Count == 0) return energyPeaks.ToList();
        var transitionScores = MeasureTransitionScores(features, normalizedFullness, sampleSeconds);
        var refined = new List<JumpWindow>();

        foreach (var peak in energyPeaks)
        {
            var peakStart = Math.Clamp((int)Math.Floor(peak.StartSeconds), 0, features.Length - 1);
            var peakEnd = Math.Clamp((int)Math.Ceiling(peak.EndSeconds), peakStart + 1, features.Length);
            var candidates = repeatedRuns
                .Where(run => Overlap(run.Start, run.End, peakStart, peakEnd) >= 3)
                .ToList();
            if (candidates.Count == 0) continue;
            var strongest = candidates.Max(run => run.Similarity);
            if (strongest < parameters.MinimumStrongestSimilarity) continue;

            var selected = candidates
                .Where(run => run.Similarity >= strongest - parameters.SimilaritySlack)
                .OrderByDescending(run => run.End - run.Start)
                .ThenByDescending(run => Overlap(run.Start, run.End, peakStart, peakEnd))
                .First();
            var start = peakStart;
            var peakSupport = RepeatSupport(features, peakStart);
            var selectedSupport = Enumerable.Range(selected.Start, Math.Max(1, selected.End - selected.Start - 6))
                .Max(position => RepeatSupport(features, position));
            var nearbyBoundary = BestBoundary(transitionScores, peakStart - 2, peakStart + 2);
            var nearbySupport = RepeatSupport(features, nearbyBoundary.Index);
            if (nearbyBoundary.Score >= parameters.TransitionThreshold &&
                nearbySupport >= selectedSupport - parameters.SupportTolerance)
                start = nearbyBoundary.Index;
            else if (selected.Start > peakStart + 3)
            {
                var forwardBoundary = BestBoundary(transitionScores, peakStart + 3,
                    Math.Min(peakEnd, selected.End));
                var forwardSupport = RepeatSupport(features, forwardBoundary.Index);
                start = forwardBoundary.Score >= parameters.TransitionThreshold &&
                    forwardSupport >= selectedSupport - parameters.SupportTolerance
                    ? forwardBoundary.Index
                    : selected.Start;
            }
            else
            {
                var backwardBoundary = BestBoundary(transitionScores,
                    Math.Max(selected.Start - 1, peakStart - 13), peakStart + 2);
                var backwardSupport = RepeatSupport(features, backwardBoundary.Index);
                if (backwardBoundary.Score >= parameters.TransitionThreshold &&
                    backwardSupport >= peakSupport + parameters.BackwardSupportGain)
                    start = backwardBoundary.Index;
            }

            var end = Math.Max(peakEnd, selected.End);
            if (end - start < MinimumJumpSeconds) continue;
            refined.Add(new JumpWindow(start, Math.Min(features.Length, end)));
        }

        if (refined.Count == 0) return [];
        refined.Sort((left, right) => left.StartSeconds.CompareTo(right.StartSeconds));
        var merged = new List<JumpWindow>();
        var current = refined[0];
        foreach (var candidate in refined.Skip(1))
        {
            if (candidate.StartSeconds - current.EndSeconds <= MaximumBeatScaleGapSeconds)
                current = new JumpWindow(current.StartSeconds, Math.Max(current.EndSeconds, candidate.EndSeconds));
            else
            {
                merged.Add(current);
                current = candidate;
            }
        }
        merged.Add(current);
        return merged;
    }

    private static double[][] NormalizeFeatures(IReadOnlyList<SpectralFrame> frames, int bandCount)
    {
        var means = new double[bandCount];
        var deviations = new double[bandCount];
        foreach (var frame in frames)
            for (var band = 0; band < bandCount; band++)
                means[band] += frame.Bands[band];
        for (var band = 0; band < bandCount; band++) means[band] /= frames.Count;
        foreach (var frame in frames)
            for (var band = 0; band < bandCount; band++)
                deviations[band] += Math.Pow(frame.Bands[band] - means[band], 2);
        for (var band = 0; band < bandCount; band++)
            deviations[band] = Math.Sqrt(deviations[band] / frames.Count) + 1e-6;

        return frames.Select(frame =>
        {
            var row = Enumerable.Range(0, bandCount)
                .Select(band => (frame.Bands[band] - means[band]) / deviations[band])
                .ToArray();
            var length = Math.Sqrt(row.Sum(value => value * value)) + 1e-9;
            for (var band = 0; band < row.Length; band++) row[band] /= length;
            return row;
        }).ToArray();
    }

    private static List<RepeatedRun> FindRepeatedRuns(IReadOnlyList<double[]> features,
        ChorusDetectionParameters parameters)
    {
        const int minimumSeparation = 24;
        const int minimumRun = 7;
        var runs = new List<RepeatedRun>();
        for (var offset = minimumSeparation; offset <= features.Count - minimumRun; offset++)
        {
            var count = features.Count - offset;
            var similarities = new double[count];
            for (var index = 0; index < count; index++)
                similarities[index] = Dot(features[index], features[index + offset]);
            var smoothed = new double[count];
            for (var index = 0; index < count; index++)
                smoothed[index] = Enumerable.Range(Math.Max(0, index - 1), Math.Min(count - 1, index + 1) - Math.Max(0, index - 1) + 1)
                    .Average(position => similarities[position]);

            var activeStart = -1;
            var inactive = 0;
            for (var index = 0; index <= count; index++)
            {
                var active = index < count && smoothed[index] >= parameters.RepeatedSimilarityThreshold;
                if (active)
                {
                    if (activeStart < 0) activeStart = index;
                    inactive = 0;
                    continue;
                }
                if (activeStart < 0) continue;
                inactive++;
                if (inactive <= 2 && index < count) continue;
                var end = index - inactive + 1;
                AddRepeatedRun(runs, smoothed, activeStart, end, offset, minimumRun,
                    parameters.MaximumRepeatedRunSeconds);
                activeStart = -1;
                inactive = 0;
            }
        }
        return runs;
    }

    private static void AddRepeatedRun(List<RepeatedRun> runs, IReadOnlyList<double> similarities,
        int start, int end, int offset, int minimumRun, int maximumRun)
    {
        var duration = end - start;
        if (duration < minimumRun || duration > maximumRun) return;
        var similarity = Enumerable.Range(start, duration).Average(index => similarities[index]);
        runs.Add(new RepeatedRun(similarity, start, end));
        runs.Add(new RepeatedRun(similarity, start + offset, end + offset));
    }

    private static double[] MeasureTransitionScores(IReadOnlyList<double[]> features,
        IReadOnlyList<double> normalizedFullness, double sampleSeconds)
    {
        var scores = new double[features.Count];
        var novelty = new double[features.Count];
        var fullness = Enumerable.Range(0, features.Count)
            .Select(second =>
            {
                var start = Math.Clamp((int)Math.Floor(second / sampleSeconds), 0, normalizedFullness.Count - 1);
                var end = Math.Clamp((int)Math.Ceiling((second + 1) / sampleSeconds), start + 1, normalizedFullness.Count);
                return normalizedFullness.Skip(start).Take(end - start).Average();
            })
            .ToArray();
        for (var index = 4; index < features.Count - 4; index++)
        {
            double squares = 0;
            for (var band = 0; band < features[index].Length; band++)
            {
                var before = Enumerable.Range(index - 4, 4).Average(position => features[position][band]);
                var after = Enumerable.Range(index, 4).Average(position => features[position][band]);
                squares += Math.Pow(before - after, 2);
            }
            novelty[index] = Math.Sqrt(squares);
        }
        var reference = Math.Max(.001, Percentile(novelty, .90));
        for (var index = 4; index < features.Count - 4; index++)
        {
            var lift = Enumerable.Range(index, 4).Average(position => fullness[position]) -
                Enumerable.Range(index - 4, 4).Average(position => fullness[position]);
            var liftWeight = Math.Clamp((lift + .08) / .35, 0, 1);
            scores[index] = novelty[index] / reference * (.45 + .55 * liftWeight);
        }
        return scores;
    }

    private static double Dot(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        double total = 0;
        for (var index = 0; index < Math.Min(left.Count, right.Count); index++) total += left[index] * right[index];
        return total;
    }

    private static double RepeatSupport(IReadOnlyList<double[]> features, int start)
    {
        const int sectionLength = 8;
        const int minimumSeparation = 24;
        start = Math.Clamp(start, 0, Math.Max(0, features.Count - sectionLength));
        var matches = new List<(double Similarity, int Start)>();
        for (var candidate = 0; candidate <= features.Count - sectionLength; candidate++)
        {
            if (Math.Abs(candidate - start) < minimumSeparation) continue;
            var similarity = Enumerable.Range(0, sectionLength)
                .Average(offset => Dot(features[start + offset], features[candidate + offset]));
            matches.Add((similarity, candidate));
        }

        var distinct = new List<(double Similarity, int Start)>();
        foreach (var match in matches.OrderByDescending(match => match.Similarity))
        {
            if (distinct.All(existing => Math.Abs(existing.Start - match.Start) >= 12)) distinct.Add(match);
            if (distinct.Count == 2) break;
        }
        return distinct.Count == 0 ? 0 : distinct.Average(match => match.Similarity);
    }

    private static int Overlap(int leftStart, int leftEnd, int rightStart, int rightEnd) =>
        Math.Max(0, Math.Min(leftEnd, rightEnd) - Math.Max(leftStart, rightStart));

    private static (int Index, double Score) BestBoundary(IReadOnlyList<double> scores, int start, int end)
    {
        start = Math.Clamp(start, 1, scores.Count - 1);
        end = Math.Clamp(end, start, scores.Count - 1);
        var index = Enumerable.Range(start, end - start + 1).MaxBy(position => scores[position]);
        return (index, scores[index]);
    }

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(value => value).ToArray();
        var position = Math.Clamp(percentile, 0, 1) * (sorted.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = Math.Min(sorted.Length - 1, lower + 1);
        var fraction = position - lower;
        return sorted[lower] + (sorted[upper] - sorted[lower]) * fraction;
    }

    private sealed record RepeatedRun(double Similarity, int Start, int End);
}
