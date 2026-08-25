namespace InnerTune;

public sealed class AudioIconAnimator
{
    public const int LevelCount = 6;
    public const int PhaseCount = 8;
    public const int FrameCount = 1 + (LevelCount - 1) * PhaseCount;

    private float _envelope;
    private float _reference = .08f;
    private double _phase;
    private readonly BeatTempoTracker _tempo = new();

    public double EstimatedBpm => _tempo.Bpm;
    public bool HasTempoEstimate => _tempo.HasEstimate;

    public int Update(float level, bool playing, bool enabled, double elapsedSeconds = .125)
    {
        if (!playing || !enabled)
        {
            Reset();
            return 0;
        }

        var input = Math.Clamp(level, 0, 1);
        var elapsed = Math.Clamp(elapsedSeconds, .025, .5);
        _tempo.Update(input, elapsed);
        _envelope += (input - _envelope) * (input > _envelope ? .62f : .22f);
        _reference = Math.Max(_envelope, Math.Max(.05f, _reference * .992f));
        if (input < .004f && _envelope < .012f) return 0;

        var normalized = Math.Clamp(_envelope / _reference, 0, 1);
        var audible = Math.Clamp(input / .045f, 0, 1);
        var energy = Math.Clamp(MathF.Sqrt(normalized) * (.35f + .65f * audible), 0, 1);
        var amplitudeLevel = Math.Clamp((int)MathF.Round(energy * (LevelCount - 1)), 1, LevelCount - 1);

        // One complete gesture spans two beats. That keeps all eight poses
        // legible at the player's low-cost 8 Hz meter rate while still making
        // fast tracks visibly more energetic than slow ones.
        _phase = (_phase + elapsed * _tempo.Bpm / 60 * PhaseCount / 2) % PhaseCount;
        return Encode(amplitudeLevel, (int)Math.Floor(_phase));
    }

    public void Reset()
    {
        _envelope = 0;
        _reference = .08f;
        _phase = 0;
        _tempo.Reset();
    }

    public static int Encode(int level, int phase)
    {
        if (level <= 0) return 0;
        return 1 + (Math.Clamp(level, 1, LevelCount - 1) - 1) * PhaseCount + Math.Clamp(phase, 0, PhaseCount - 1);
    }
}
