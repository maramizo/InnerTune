using NAudio.Wave;

namespace InnerTune;

/// <summary>
/// Pass-through sample provider that emits one inexpensive RMS loudness value
/// per notification window. RMS tracks sustained musical energy much better
/// than a single peak, so isolated piano transients do not look like a drop.
/// </summary>
public sealed class RmsMeteringSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _samplesPerNotification;
    private double _squareSum;
    private int _sampleCount;

    public RmsMeteringSampleProvider(ISampleProvider source, int samplesPerNotification)
    {
        _source = source;
        _samplesPerNotification = Math.Max(1, samplesPerNotification);
    }

    public WaveFormat WaveFormat => _source.WaveFormat;
    public event Action<float>? LoudnessAvailable;

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        var end = offset + read;
        for (var index = offset; index < end; index++)
        {
            var sample = buffer[index];
            _squareSum += sample * sample;
            _sampleCount++;
            if (_sampleCount < _samplesPerNotification) continue;
            LoudnessAvailable?.Invoke((float)Math.Sqrt(_squareSum / _sampleCount));
            _squareSum = 0;
            _sampleCount = 0;
        }
        return read;
    }
}
