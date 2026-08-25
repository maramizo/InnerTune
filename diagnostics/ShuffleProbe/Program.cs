using System.Text.Json;
using InnerTune;

const int count = 12;
const int startingSong = 4;
var navigator = new ShuffleNavigator(new Random(1729));
navigator.Reset(count, startingSong);
var current = startingSong;
var cycle = new List<int> { current };
while (navigator.TryNext(count, current, false, out var next))
{
    if (cycle.Contains(next)) throw new InvalidOperationException($"Shuffle repeated song {next} before completing its cycle.");
    cycle.Add(next);
    current = next;
}
if (cycle.Count != count || cycle.Distinct().Count() != count)
    throw new InvalidOperationException("Shuffle did not play every queued song exactly once.");
if (navigator.TryNext(count, current, false, out _))
    throw new InvalidOperationException("Shuffle continued after a non-repeating cycle ended.");
if (!navigator.TryNext(count, current, true, out var newCycleSong) || newCycleSong == current)
    throw new InvalidOperationException("Shuffle did not begin a fresh cycle correctly.");
if (!navigator.TryPrevious(count, newCycleSong, out var previous) || previous != current)
    throw new InvalidOperationException("Shuffle history did not return to the previous song.");
if (!navigator.TryNext(count, previous, false, out var forwardAgain) || forwardAgain != newCycleSong)
    throw new InvalidOperationException("Shuffle history did not preserve forward navigation.");

Console.WriteLine(JsonSerializer.Serialize(new
{
    passed = true,
    songs = count,
    uniqueSongsInCycle = cycle.Distinct().Count(),
    order = cycle,
    stoppedAtCycleEnd = true,
    previousAndForwardPreserved = true
}));
