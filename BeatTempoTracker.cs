namespace InnerTune;

/// <summary>
/// Estimates musical tempo from the low-rate level samples already produced by
/// the player. This deliberately avoids a second decoder or an FFT so the tray
/// animation stays inexpensive while the main window is hidden.
/// </summary>
public sealed class BeatTempoTracker
{
    public const double DefaultBpm = 108;
    public const double MinimumBpm = 68;
    public const double MaximumBpm = 190;

    private readonly Queue<double> _recentBpms = new();
    private double _clock;
    private double _lastBeatAt = double.NegativeInfinity;
    private float _floor = .02f;
    private float _previous;
    private float _previousPrevious;

    public double Bpm { get; private set; } = DefaultBpm;
    public bool HasEstimate => _recentBpms.Count >= 2;

    public void Update(float level, double elapsedSeconds)
    {
        var elapsed = Math.Clamp(elapsedSeconds, .025, .5);
        _clock += elapsed;
        level = Math.Clamp(level, 0, 1);

        var thresholdFloor = _floor;
        _floor += (level - _floor) * (level > _floor ? .025f : .12f);

        // A local maximum is much less likely to double-trigger than a simple
        // threshold crossing. The refractory period still permits fast music.
        var peakAt = _clock - elapsed;
        var prominence = _previous - _previousPrevious;
        var isBeat = _previous > _previousPrevious &&
            _previous >= level &&
            _previous > Math.Max(.0075f, thresholdFloor * 1.18f) &&
            prominence > Math.Max(.0015f, thresholdFloor * .055f) &&
            peakAt - _lastBeatAt >= .24;

        if (isBeat)
        {
            if (!double.IsNegativeInfinity(_lastBeatAt))
            {
                var candidate = 60 / (peakAt - _lastBeatAt);
                while (candidate < MinimumBpm) candidate *= 2;
                while (candidate > MaximumBpm) candidate /= 2;

                if (candidate is >= MinimumBpm and <= MaximumBpm)
                {
                    _recentBpms.Enqueue(candidate);
                    while (_recentBpms.Count > 8) _recentBpms.Dequeue();

                    var ordered = _recentBpms.OrderBy(value => value).ToArray();
                    var middle = ordered.Length / 2;
                    var median = ordered.Length % 2 == 0
                        ? (ordered[middle - 1] + ordered[middle]) / 2
                        : ordered[middle];
                    Bpm += (median - Bpm) * (_recentBpms.Count < 3 ? .55 : .28);
                }
            }
            _lastBeatAt = peakAt;
        }

        _previousPrevious = _previous;
        _previous = level;
    }

    public void Reset()
    {
        _recentBpms.Clear();
        _clock = 0;
        _lastBeatAt = double.NegativeInfinity;
        _floor = .02f;
        _previous = 0;
        _previousPrevious = 0;
        Bpm = DefaultBpm;
    }
}
