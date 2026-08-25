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

    public static List<JumpWindow> Plan(
        IReadOnlyList<float> envelope,
        double sampleSeconds,
        double fullnessFloor,
        double fullnessCeiling,
        double danceability)
    {
        if (envelope.Count == 0 || sampleSeconds <= 0) return [];
        var range = Math.Max(.025, fullnessCeiling - fullnessFloor);
        var raveCharacter = SmoothStep(.42, .72, danceability);
        var sustained = 0d;
        var requested = false;
        var activeStart = -1d;
        var candidates = new List<JumpWindow>();

        for (var index = 0; index < envelope.Count; index++)
        {
            var input = Math.Clamp(envelope[index], 0, 1);
            var baselineTicks = sampleSeconds * 15;
            var blend = 1 - Math.Pow(1 - (input > sustained ? .18 : .08), baselineTicks);
            sustained += (input - sustained) * blend;
            var fullness = Math.Clamp((sustained - fullnessFloor) / range, 0, 1);
            var energy = raveCharacter * Math.Pow(fullness, AudioIconAnimator.FullnessExponent);
            if (!requested && energy >= AudioIconAnimator.JumpStartThreshold) requested = true;
            else if (requested && energy <= AudioIconAnimator.JumpStopThreshold) requested = false;

            var at = index * sampleSeconds;
            if (requested && activeStart < 0) activeStart = at;
            else if (!requested && activeStart >= 0)
            {
                candidates.Add(new JumpWindow(activeStart, at));
                activeStart = -1;
            }
        }
        if (activeStart >= 0) candidates.Add(new JumpWindow(activeStart, envelope.Count * sampleSeconds));
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

    private static double SmoothStep(double low, double high, double value)
    {
        var position = Math.Clamp((value - low) / (high - low), 0, 1);
        return position * position * (3 - 2 * position);
    }
}
