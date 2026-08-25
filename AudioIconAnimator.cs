namespace InnerTune;

public sealed class AudioIconAnimator
{
    public const int LevelCount = 6;
    public const int PhaseCount = 8;
    public const int FrameCount = 1 + (LevelCount - 1) * PhaseCount;

    private float _envelope;
    private float _reference = .08f;
    private int _phase = -1;

    public int Update(float level, bool playing, bool enabled)
    {
        if (!playing || !enabled)
        {
            Reset();
            return 0;
        }

        var input = Math.Clamp(level, 0, 1);
        _envelope += (input - _envelope) * (input > _envelope ? .62f : .22f);
        _reference = Math.Max(_envelope, Math.Max(.05f, _reference * .992f));
        if (input < .004f && _envelope < .012f) return 0;

        var normalized = Math.Clamp(_envelope / _reference, 0, 1);
        var audible = Math.Clamp(input / .045f, 0, 1);
        var energy = Math.Clamp(MathF.Sqrt(normalized) * (.35f + .65f * audible), 0, 1);
        var amplitudeLevel = Math.Clamp((int)MathF.Round(energy * (LevelCount - 1)), 1, LevelCount - 1);
        _phase = (_phase + 1) % PhaseCount;
        return Encode(amplitudeLevel, _phase);
    }

    public void Reset()
    {
        _envelope = 0;
        _reference = .08f;
        _phase = -1;
    }

    public static int Encode(int level, int phase)
    {
        if (level <= 0) return 0;
        return 1 + (Math.Clamp(level, 1, LevelCount - 1) - 1) * PhaseCount + Math.Clamp(phase, 0, PhaseCount - 1);
    }
}
