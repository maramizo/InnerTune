namespace InnerTune;

public sealed class ShuffleNavigator
{
    private readonly Random _random;
    private readonly List<int> _upcoming = [];
    private readonly Stack<int> _history = [];

    public ShuffleNavigator() : this(Random.Shared) { }
    public ShuffleNavigator(Random random) => _random = random;

    public void Reset(int count, int current)
    {
        _history.Clear();
        Fill(count, current);
    }

    public void Clear()
    {
        _upcoming.Clear();
        _history.Clear();
    }

    public bool TryNext(int count, int current, bool allowNewCycle, out int next)
    {
        RemoveInvalid(count);
        if (_upcoming.Count == 0)
        {
            if (!allowNewCycle) { next = -1; return false; }
            Fill(count, current);
        }

        if (_upcoming.Count == 0)
        {
            next = current;
            return current >= 0 && current < count;
        }

        next = _upcoming[0];
        _upcoming.RemoveAt(0);
        if (current >= 0 && current < count) _history.Push(current);
        return true;
    }

    public bool TryPrevious(int count, int current, out int previous)
    {
        while (_history.TryPop(out previous))
        {
            if (previous < 0 || previous >= count) continue;
            if (current >= 0 && current < count && !_upcoming.Contains(current)) _upcoming.Insert(0, current);
            return true;
        }
        previous = -1;
        return false;
    }

    private void Fill(int count, int current)
    {
        _upcoming.Clear();
        for (var index = 0; index < count; index++)
            if (index != current) _upcoming.Add(index);
        for (var index = _upcoming.Count - 1; index > 0; index--)
        {
            var other = _random.Next(index + 1);
            (_upcoming[index], _upcoming[other]) = (_upcoming[other], _upcoming[index]);
        }
    }

    private void RemoveInvalid(int count) => _upcoming.RemoveAll(index => index < 0 || index >= count);
}
