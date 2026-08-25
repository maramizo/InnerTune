namespace InnerTune;

public static class PlaybackRestorePolicy
{
    public static bool ShouldAutoResume(PlaybackSnapshot snapshot, AppSettings settings) =>
        settings.AutoResumeOnStart &&
        snapshot.Status.Equals("playing", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(snapshot.TrackId);
}
