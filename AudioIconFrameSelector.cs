namespace InnerTune;

public static class AudioIconFrameSelector
{
    public static int Select(float level, bool playing, bool enabled) => !playing || !enabled
        ? 0
        : Math.Clamp(level, 0, 1) switch { < .12f => 0, < .45f => 1, _ => 2 };
}
