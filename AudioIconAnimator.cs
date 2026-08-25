namespace InnerTune;

public sealed class AudioIconAnimator
{
    public const int LevelCount = 6;
    public const int PhaseCount = 12;
    public const int JumpFrameOffset = 1 + (LevelCount - 1) * PhaseCount;
    public const int FrameCount = JumpFrameOffset + PhaseCount;
    public const double JumpStartThreshold = .38;
    public const double JumpStopThreshold = .22;

    private float _envelope;
    private float _reference = .08f;
    private double _phase;
    private double _bpm = BeatTempoTracker.DefaultBpm;
    private int _activeAmplitudeLevel;
    private int _pendingAmplitudeLevel;
    private int _lastRenderedPhase = -1;
    private bool _activeJump;
    private bool _jumpRequested;
    private float _sustainedLoudness;
    private float _songMaximumLoudness = .08f;
    private double _danceability;

    public double EstimatedBpm => _bpm;
    public bool HasTempoEstimate { get; private set; }

    public int Update(float level, bool playing, bool enabled, double elapsedSeconds = .125)
    {
        if (!playing || !enabled)
        {
            Reset();
            return 0;
        }

        var input = Math.Clamp(level, 0, 1);
        var elapsed = Math.Clamp(elapsedSeconds, .025, .5);
        _envelope += (input - _envelope) * (input > _envelope ? .62f : .22f);
        _reference = Math.Max(_envelope, Math.Max(.05f, _reference * .992f));

        var normalized = Math.Clamp(_envelope / _reference, 0, 1);
        var audible = Math.Clamp(input / .045f, 0, 1);
        var energy = Math.Clamp(MathF.Sqrt(normalized) * (.35f + .65f * audible), 0, 1);
        _pendingAmplitudeLevel = input < .004f && _envelope < .012f
            ? 0
            : Math.Clamp((int)MathF.Round(energy * (LevelCount - 1)), 1, LevelCount - 1);

        _sustainedLoudness += (input - _sustainedLoudness) * (input > _sustainedLoudness ? .18f : .08f);
        _songMaximumLoudness = Math.Max(_songMaximumLoudness, _sustainedLoudness);
        var relativeLoudness = Math.Clamp(_sustainedLoudness / Math.Max(.04f, _songMaximumLoudness), 0, 1);
        var absoluteGate = SmoothStep(.07, .18, _sustainedLoudness);
        var raveCharacter = SmoothStep(.42, .72, _danceability);
        var raveEnergy = raveCharacter * Math.Pow(relativeLoudness, 2.2) * absoluteGate;
        if (!_jumpRequested && raveEnergy >= JumpStartThreshold) _jumpRequested = true;
        else if (_jumpRequested && raveEnergy <= JumpStopThreshold) _jumpRequested = false;

        var phase = (int)Math.Floor(_phase);
        if (_activeAmplitudeLevel == 0 && _pendingAmplitudeLevel > 0)
        {
            // Always enter the choreography from a grounded pose, even when
            // audio starts between the normal rest points.
            _phase = 0;
            phase = 0;
            _lastRenderedPhase = -1;
        }

        if (IsRestPhase(phase) && phase != _lastRenderedPhase)
        {
            // Changing pose banks in the air visually teleports the paws and
            // reads as a stutter. Loudness and jumping therefore only change
            // while both paws and the cat are back on the deck.
            _activeAmplitudeLevel = _pendingAmplitudeLevel;
            _activeJump = _jumpRequested && _activeAmplitudeLevel == LevelCount - 1;
        }

        var frame = _activeAmplitudeLevel <= 0
            ? 0
            : _activeJump
                ? EncodeJump(phase)
                : Encode(_activeAmplitudeLevel, phase);

        // One complete gesture spans two beats. That keeps all twelve poses
        // legible at the player's low-cost 15 Hz meter rate while still making
        // fast tracks visibly more energetic than slow ones. Never skip a pose:
        // the grounded frames are also the safe state-transition boundary.
        var phaseAdvance = Math.Min(1, elapsed * _bpm / 60 * PhaseCount / 2);
        _lastRenderedPhase = phase;
        _phase = (_phase + phaseAdvance) % PhaseCount;
        return frame;
    }

    public void SetTempo(double bpm)
    {
        _bpm = Math.Clamp(bpm, BeatTempoTracker.MinimumBpm, BeatTempoTracker.MaximumBpm);
        HasTempoEstimate = true;
    }

    public void SetMotionProfile(double danceability, double peakLoudness)
    {
        _danceability = Math.Clamp(danceability, 0, 1);
        _songMaximumLoudness = Math.Max(_songMaximumLoudness, (float)Math.Clamp(peakLoudness, .04, 1));
    }

    public void Reset()
    {
        _envelope = 0;
        _reference = .08f;
        _phase = 0;
        _activeAmplitudeLevel = 0;
        _pendingAmplitudeLevel = 0;
        _lastRenderedPhase = -1;
        _activeJump = false;
        _jumpRequested = false;
        _sustainedLoudness = 0;
    }

    public void ResetTempo()
    {
        Reset();
        _bpm = BeatTempoTracker.DefaultBpm;
        _songMaximumLoudness = .08f;
        _danceability = 0;
        HasTempoEstimate = false;
    }

    public static int Encode(int level, int phase)
    {
        if (level <= 0) return 0;
        return 1 + (Math.Clamp(level, 1, LevelCount - 1) - 1) * PhaseCount + Math.Clamp(phase, 0, PhaseCount - 1);
    }

    public static int EncodeJump(int phase) => JumpFrameOffset + Math.Clamp(phase, 0, PhaseCount - 1);

    public static bool IsJumpFrame(int frame) => frame >= JumpFrameOffset && frame < FrameCount;

    public static int DecodePhase(int frame) => frame <= 0
        ? 0
        : IsJumpFrame(frame)
            ? frame - JumpFrameOffset
            : (frame - 1) % PhaseCount;

    public static int DecodeLevel(int frame) => frame <= 0
        ? 0
        : IsJumpFrame(frame)
            ? LevelCount - 1
            : (frame - 1) / PhaseCount + 1;

    public static bool IsRestPhase(int phase) => phase == 0 || phase == PhaseCount / 2;

    private static double SmoothStep(double low, double high, double value)
    {
        var position = Math.Clamp((value - low) / (high - low), 0, 1);
        return position * position * (3 - 2 * position);
    }
}
