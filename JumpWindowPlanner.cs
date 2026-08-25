namespace InnerTune;

public sealed record JumpWindow(double StartSeconds, double EndSeconds)
{
    public double DurationSeconds => Math.Max(0, EndSeconds - StartSeconds);
    public bool Contains(double positionSeconds) => positionSeconds >= StartSeconds && positionSeconds < EndSeconds;
}

public static class JumpWindowPlanner
{
    public const double MinimumJumpSeconds = 5;
    private const double MaximumBeatScaleGapSeconds = 1.25;
    private const double MinimumPeakAverage = .45;

    public static List<JumpWindow> Plan(
        IReadOnlyList<float> envelope,
        double sampleSeconds,
        double fullnessFloor,
        double fullnessCeiling,
        DanceMetrics danceMetrics)
    {
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
        var targetFraction = .08 + .43 * Math.Pow(Math.Clamp(danceMetrics.Score, 0, 1), 4);
        var peakAverage = Math.Max(MinimumPeakAverage, Percentile(rollingAverages, 1 - targetFraction));
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
        return merged.Where(window => window.DurationSeconds >= MinimumJumpSeconds).ToList();
    }

    public static bool IsJumpWorthy(DanceMetrics metrics)
    {
        var rhythmicPulse = Math.Max(metrics.Pulse, Math.Max(metrics.OnsetPulse, metrics.BroadOnsetPulse));
        var denseRock = metrics.DenseRhythmicEnergy >= .45;
        var bassDriven = metrics.SustainedEnergy >= .65 && rhythmicPulse >= .40 && metrics.BassRhythm >= .32;
        var percussive = metrics.OnsetPulse >= .20 && rhythmicPulse >= .28 && metrics.TransientStrength >= .18;
        return denseRock || bassDriven || percussive;
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
}
