namespace InnerTune;

public sealed class AudioIconAnimator
{
    public const int LevelCount = 6;
    public const int PhaseCount = 24;
    public const int JumpFrameOffset = 1 + (LevelCount - 1) * PhaseCount;
    public const int FrameCount = JumpFrameOffset + PhaseCount;
    public const double JumpStartThreshold = .62;
    public const double JumpStopThreshold = .44;
    public const double FullnessExponent = 4.2;

    private float _envelope;
    private float _reference = .08f;
    private double _phase;
    private double _bpm = BeatTempoTracker.DefaultBpm;
    private int _activeAmplitudeLevel;
    private int _pendingAmplitudeLevel;
    private long _lastStrengthMarker = long.MinValue;
    private long _lastJumpMarker = long.MinValue;
    private long _jumpStartedMarker = long.MinValue;
    private long _jumpEndedMarker = long.MinValue;
    private bool _activeJump;
    private bool _jumpRequested;
    private float _sustainedLoudness;
    private float _fullnessFloor = .04f;
    private float _fullnessCeiling = .18f;
    private double _danceability;
    private IReadOnlyList<JumpWindow> _jumpWindows = [];
    private bool _hasJumpPlan;

    public double EstimatedBpm => _bpm;
    public bool HasTempoEstimate { get; private set; }

    public int Update(float level, bool playing, bool enabled, double elapsedSeconds = .125,
        double? playbackPositionSeconds = null)
    {
        if (!playing || !enabled)
        {
            Reset();
            return 0;
        }

        var input = Math.Clamp(level, 0, 1);
        var elapsed = Math.Clamp(elapsedSeconds, .025, .5);
        var baselineTicks = elapsed * 15;
        _envelope += (input - _envelope) * TimeAdjustedBlend(input > _envelope ? .62 : .22, baselineTicks);
        _reference = Math.Max(_envelope, Math.Max(.05f, _reference * (float)Math.Pow(.992, baselineTicks)));

        var normalized = Math.Clamp(_envelope / _reference, 0, 1);
        var audible = Math.Clamp(input / .045f, 0, 1);
        var energy = Math.Clamp(MathF.Sqrt(normalized) * (.35f + .65f * audible), 0, 1);
        var measuredAmplitudeLevel = input < .004f && _envelope < .012f
            ? 0
            : Math.Clamp((int)MathF.Round(energy * (LevelCount - 1)), 1, LevelCount - 1);
        if (!_activeJump) _pendingAmplitudeLevel = measuredAmplitudeLevel;

        _sustainedLoudness += (input - _sustainedLoudness) * TimeAdjustedBlend(input > _sustainedLoudness ? .18 : .08, baselineTicks);
        var fullnessRange = Math.Max(.025f, _fullnessCeiling - _fullnessFloor);
        var relativeFullness = Math.Clamp((_sustainedLoudness - _fullnessFloor) / fullnessRange, 0, 1);
        var raveCharacter = SmoothStep(.42, .72, _danceability);
        var raveEnergy = raveCharacter * Math.Pow(relativeFullness, FullnessExponent);
        if (_hasJumpPlan && playbackPositionSeconds is { } playbackPosition)
            _jumpRequested = _jumpWindows.Any(window => window.Contains(playbackPosition));
        else
        {
            if (!_jumpRequested && raveEnergy >= JumpStartThreshold) _jumpRequested = true;
            else if (_jumpRequested && raveEnergy <= JumpStopThreshold) _jumpRequested = false;
        }

        var strengthMarker = (long)Math.Floor(_phase / (PhaseCount / 4d));
        if (!_activeJump && strengthMarker != _lastStrengthMarker)
        {
            // A hand-motion bank may change only at one of the four crossover
            // markers in a full gesture. This keeps loudness reactive without
            // moving a paw to a different path halfway through its stroke.
            _activeAmplitudeLevel = _pendingAmplitudeLevel;
            _lastStrengthMarker = strengthMarker;
        }

        var jumpMarker = (long)Math.Floor(_phase / (PhaseCount / 2d));
        if (jumpMarker != _lastJumpMarker)
        {
            var enterJump = !_activeJump && _jumpRequested &&
                (_jumpEndedMarker == long.MinValue || jumpMarker - _jumpEndedMarker >= 2);
            var leaveJump = _activeJump && !_jumpRequested &&
                jumpMarker - _jumpStartedMarker >= MinimumJumpMarkers();
            if (enterJump || leaveJump)
            {
                // A delayed Windows tick can cross a marker. Render the actual
                // takeoff/landing pose once instead of changing clips in midair.
                _phase = jumpMarker * (PhaseCount / 2d);
                _activeJump = enterJump;
                if (enterJump) _jumpStartedMarker = jumpMarker;
                else
                {
                    _jumpEndedMarker = jumpMarker;
                    _pendingAmplitudeLevel = measuredAmplitudeLevel;
                    _activeAmplitudeLevel = measuredAmplitudeLevel;
                    _lastStrengthMarker = strengthMarker;
                }
            }
            _lastJumpMarker = jumpMarker;
        }

        var phase = (int)Math.Floor(_phase % PhaseCount);
        var frame = _activeJump
            ? EncodeJump(phase)
            : _activeAmplitudeLevel <= 0
                ? 0
                : Encode(_activeAmplitudeLevel, phase);

        // One complete gesture spans two beats. Twenty-four poses at the
        // player's 30 Hz meter rate keep motion fluid while still making
        // fast tracks visibly more energetic than slow ones.
        // If Windows delays a render tick (for example while a cache transfer
        // finishes), catch up to musical time instead of replaying stale poses
        // in slow motion.
        var phaseAdvance = elapsed * _bpm / 60 * PhaseCount / 2;
        _phase += phaseAdvance;
        return frame;
    }

    public void SetTempo(double bpm)
    {
        _bpm = Math.Clamp(bpm, BeatTempoTracker.MinimumBpm, BeatTempoTracker.MaximumBpm);
        HasTempoEstimate = true;
    }

    public void SetMotionProfile(double danceability, double fullnessFloor, double fullnessCeiling,
        IReadOnlyList<JumpWindow>? jumpWindows = null)
    {
        _danceability = Math.Clamp(danceability, 0, 1);
        _fullnessFloor = (float)Math.Clamp(fullnessFloor, 0, .95);
        _fullnessCeiling = (float)Math.Clamp(fullnessCeiling, _fullnessFloor + .025f, 1);
        _jumpWindows = jumpWindows ?? [];
        _hasJumpPlan = jumpWindows is not null;
    }

    public void Reset()
    {
        _envelope = 0;
        _reference = .08f;
        _phase = 0;
        _activeAmplitudeLevel = 0;
        _pendingAmplitudeLevel = 0;
        _lastStrengthMarker = long.MinValue;
        _lastJumpMarker = long.MinValue;
        _jumpStartedMarker = long.MinValue;
        _jumpEndedMarker = long.MinValue;
        _activeJump = false;
        _jumpRequested = false;
        _sustainedLoudness = 0;
    }

    public void ResetTempo()
    {
        Reset();
        _bpm = BeatTempoTracker.DefaultBpm;
        _fullnessFloor = .04f;
        _fullnessCeiling = .18f;
        _danceability = 0;
        _jumpWindows = [];
        _hasJumpPlan = false;
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

    public static bool IsStrengthMarker(int phase) => phase % (PhaseCount / 4) == 0;

    public static bool IsJumpMarker(int phase) => phase % (PhaseCount / 2) == 0;

    private long MinimumJumpMarkers() => Math.Max(2, (long)Math.Ceiling(
        JumpWindowPlanner.MinimumJumpSeconds * _bpm / 60));

    private static double SmoothStep(double low, double high, double value)
    {
        var position = Math.Clamp((value - low) / (high - low), 0, 1);
        return position * position * (3 - 2 * position);
    }

    private static float TimeAdjustedBlend(double baselineBlend, double baselineTicks) =>
        (float)(1 - Math.Pow(1 - baselineBlend, baselineTicks));
}
