using System.Text.Json;
using InnerTune;

static Track Song(string id) => new() { Id = id, Title = id };
static void Expect(IList<Track> queue, params string[] ids)
{
    if (!queue.Select(track => track.Id).SequenceEqual(ids))
        throw new InvalidOperationException($"Expected [{string.Join(",", ids)}], got [{string.Join(",", queue.Select(track => track.Id))}].");
}

var current = Song("current");
var added = Song("added");
var next = Song("next");
var queue = new List<Track>();

QueueFlow.StartStandalone(queue, current);
Expect(queue, "current");
QueueFlow.Add(queue, current, added);
Expect(queue, "current", "added");
var destination = QueueFlow.PutNext(queue, current, 0, next);
if (destination != 1) throw new InvalidOperationException("Play next did not target the position after the current song.");
Expect(queue, "current", "next", "added");

destination = QueueFlow.PutNext(queue, current, 0, added);
if (destination != 1) throw new InvalidOperationException("Play next did not move an existing queued song.");
Expect(queue, "current", "added", "next");

var detached = new List<Track> { Song("unrelated") };
QueueFlow.Add(detached, current, added);
Expect(detached, "current", "unrelated", "added");

Console.WriteLine(JsonSerializer.Serialize(new
{
    passed = true,
    standaloneBecomesAdHoc = true,
    playNextMovesExistingSong = true,
    detachedCurrentIsPreserved = true
}));
