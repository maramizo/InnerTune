namespace InnerTune;

public static class QueueFlow
{
    public static void StartStandalone(IList<Track> queue, Track track)
    {
        queue.Clear();
        queue.Add(track);
    }

    public static void Add(IList<Track> queue, Track? current, Track track)
    {
        EnsureCurrent(queue, current);
        queue.Add(track);
    }

    public static int PutNext(IList<Track> queue, Track? current, int currentIndex, Track track)
    {
        EnsureCurrent(queue, current);
        currentIndex = ResolveCurrentIndex(queue, current, currentIndex);

        var existing = IndexOfReference(queue, track);
        if (existing < 0)
            existing = Enumerable.Range(0, queue.Count).FirstOrDefault(index =>
                index != currentIndex && queue[index].Id == track.Id, -1);
        if (existing == currentIndex) return -1;

        if (existing >= 0)
        {
            queue.RemoveAt(existing);
            if (existing < currentIndex) currentIndex--;
        }
        else if (current?.Id == track.Id)
        {
            return -1;
        }

        var destination = Math.Clamp(currentIndex + 1, 0, queue.Count);
        queue.Insert(destination, track);
        return destination;
    }

    private static void EnsureCurrent(IList<Track> queue, Track? current)
    {
        if (current is null || queue.Any(track => track.Id == current.Id)) return;
        queue.Insert(0, current);
    }

    private static int ResolveCurrentIndex(IList<Track> queue, Track? current, int suggested)
    {
        if (suggested >= 0 && suggested < queue.Count &&
            (current is null || queue[suggested].Id == current.Id)) return suggested;
        return current is null ? -1 : Enumerable.Range(0, queue.Count)
            .FirstOrDefault(index => queue[index].Id == current.Id, -1);
    }

    private static int IndexOfReference(IList<Track> queue, Track track)
    {
        for (var index = 0; index < queue.Count; index++)
            if (ReferenceEquals(queue[index], track)) return index;
        return -1;
    }
}
