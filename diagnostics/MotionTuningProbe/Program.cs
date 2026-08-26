using System.Text.Json;
using InnerTune;

var populationSize = ReadOption("--population", 18, 8, 80);
var generations = ReadOption("--generations", 10, 1, 100);
var cacheDirectory = ReadTextOption("--cache") ??
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InnerTune", "audio-cache");
var corpus = new[]
{
    Labeled("vIOO_7DLr3M", "BURN IT DOWN", (68, 90), (129, 150), (173, 211)),
    Labeled("KFA1hM91ffo", "In the End", (54, 74), (109, 130), (165, 186)),
    Labeled("8ZBnwBVjwOk", "New Divide", (67, 104), (139, 161), (196, 250)),
    Labeled("KwN_f0fTHoE", "One Step Closer", (43, 55), (78, 100), (131, 155)),
    Labeled("yOW7Eh81Gto", "Faint", (52, 68), (91, 107), (121, 161)),
    Labeled("eXRWHzV72sU", "What I've Done", (64, 85), (104, 122), (162, 180)),
    Labeled("_e7bqZGPyFI", "Breaking the Habit", (47, 73), (105, 130), (148, 180)),
    Labeled("PIidgyl8U9s", "Bleed It Out", (37, 52), (78, 93), (116, 144)),
    Labeled("dxp9w9Ggehc", "Numb", (52, 70), (104, 122), (141, 176)),
    Labeled("gx-fg-dMROg", "Crawling", (24, 42), (88, 106), (145, 202)),
    Labeled("-YQ8IbVIwPM", "Somewhere I Belong", (68, 92), (115, 139), (167, 212)),
    Labeled("6xzN8Nt0Pok", "I Wanna Dance with Somebody", (62, 96), (129, 163), (192, 286)),
    Grounded("5w3rRFWzjcM", "Sweden"),
    Grounded("gLgUesz8444", "An Ending"),
    Grounded("Jd8w8iPWGM8", "Home (Music Box)"),
    Grounded("AvDrW4JTjME", "Fallen Down (Reprise)"),
    Grounded("sJhnVunhNZY", "Dire Dire Docks"),
    Grounded("K892qn3U524", "Ezio's Family"),
    Grounded("H1LdQntDnFY", "One More Light"),
    Grounded("Lp-TdtDYGAA", "At Doom's Gate"),
    Grounded("813-3iL5OsE", "Dragonborn"),
    Grounded("1RVAJ2ZPTFQ", "Ocarina Of Time"),
    Grounded("EIaLT43HX9o", "The Legend Of Zelda - Main Theme"),
    Grounded("gt1pKrwxAJU", "Opus")
};

Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
var prepared = new List<PreparedTrack>();
foreach (var item in corpus)
{
    var path = Path.Combine(cacheDirectory, $"{item.Id}.playable.m4a");
    if (!File.Exists(path)) continue;
    var input = await RepresentativeTempoAnalyzer.PrepareMotionPlanningAsync(path);
    if (input is not null) prepared.Add(new PreparedTrack(item, input));
}
if (prepared.Count < 12) throw new InvalidOperationException($"Only {prepared.Count} labeled tracks could be prepared.");

var bounds = new (double Low, double High)[]
{
    (.03, .16),
    (.22, .62),
    (2.2, 7.0),
    (.30, .64),
    (.54, .73),
    (.64, .86),
    (.01, .09),
    (.35, 1.05),
    (.01, .14),
    (.01, .14),
    (24, 42)
};
var random = new Random(0x1A2B3C);
var defaultVector = ToVector(ChorusDetectionParameters.Default);
var population = Enumerable.Range(0, populationSize)
    .Select(index => index == 0
        ? defaultVector
        : bounds.Select(bound => bound.Low + random.NextDouble() * (bound.High - bound.Low)).ToArray())
    .ToArray();
var scores = population.Select(vector => Score(prepared, ToParameters(vector))).ToArray();
var defaultScore = scores[0];
var precisionFloor = defaultScore.Precision;

for (var generation = 0; generation < generations; generation++)
{
    for (var target = 0; target < population.Length; target++)
    {
        var choices = Enumerable.Range(0, population.Length).Where(index => index != target)
            .OrderBy(_ => random.Next()).Take(3).ToArray();
        var mutant = Enumerable.Range(0, bounds.Length)
            .Select(dimension => population[choices[0]][dimension] +
                .72 * (population[choices[1]][dimension] - population[choices[2]][dimension]))
            .Select((value, dimension) => Math.Clamp(value, bounds[dimension].Low, bounds[dimension].High))
            .ToArray();
        var forcedDimension = random.Next(bounds.Length);
        var trial = Enumerable.Range(0, bounds.Length)
            .Select(dimension => dimension == forcedDimension || random.NextDouble() < .72
                ? mutant[dimension]
                : population[target][dimension])
            .ToArray();
        var trialScore = Score(prepared, ToParameters(trial));
        if (OptimizationLoss(trialScore, precisionFloor) >= OptimizationLoss(scores[target], precisionFloor))
            continue;
        population[target] = trial;
        scores[target] = trialScore;
    }
    var best = scores.MinBy(score => OptimizationLoss(score, precisionFloor))!;
    Console.Error.WriteLine($"generation {generation + 1}/{generations}: objective={OptimizationLoss(best, precisionFloor):F5}, precision={best.Precision:P1}, recall={best.Recall:P1}");
}

var bestIndex = Enumerable.Range(0, scores.Length)
    .Where(index => scores[index].Precision >= precisionFloor)
    .MinBy(index => OptimizationLoss(scores[index], precisionFloor));
var bestParameters = ToParameters(population[bestIndex]);
var bestScore = Score(prepared, bestParameters, true);
var paretoFront = Enumerable.Range(0, scores.Length)
    .Where(index => !Enumerable.Range(0, scores.Length).Any(other => other != index &&
        scores[other].Precision >= scores[index].Precision &&
        scores[other].Recall >= scores[index].Recall &&
        (scores[other].Precision > scores[index].Precision || scores[other].Recall > scores[index].Recall)))
    .OrderByDescending(index => scores[index].Precision)
    .Select(index => new
    {
        score = scores[index],
        objective = OptimizationLoss(scores[index], precisionFloor),
        parameters = ToParameters(population[index])
    })
    .ToArray();
Console.WriteLine(JsonSerializer.Serialize(new
{
    tracks = prepared.Count,
    generations,
    populationSize,
    precisionFloor,
    defaultScore,
    bestScore,
    objectiveImprovement = OptimizationLoss(defaultScore, precisionFloor) -
        OptimizationLoss(bestScore, precisionFloor),
    parameters = bestParameters,
    paretoFront
}, new JsonSerializerOptions { WriteIndented = true }));

int ReadOption(string name, int fallback, int minimum, int maximum)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var value)
        ? Math.Clamp(value, minimum, maximum)
        : fallback;
}

string? ReadTextOption(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static CorpusTrack Labeled(string id, string title, params (double Start, double End)[] choruses) =>
    new(id, title, choruses.Select(chorus => new JumpWindow(chorus.Start, chorus.End)).ToArray());

static CorpusTrack Grounded(string id, string title) => new(id, title, []);

static double[] ToVector(ChorusDetectionParameters parameters) =>
[
    parameters.PeakTargetBase,
    parameters.PeakTargetScale,
    parameters.PeakTargetExponent,
    parameters.MinimumPeakAverage,
    parameters.RepeatedSimilarityThreshold,
    parameters.MinimumStrongestSimilarity,
    parameters.SimilaritySlack,
    parameters.TransitionThreshold,
    parameters.SupportTolerance,
    parameters.BackwardSupportGain,
    parameters.MaximumRepeatedRunSeconds
];

static ChorusDetectionParameters ToParameters(IReadOnlyList<double> vector) => new(
    vector[0], vector[1], vector[2], vector[3], vector[4], vector[5], vector[6], vector[7], vector[8], vector[9],
    (int)Math.Round(vector[10]));

static double OptimizationLoss(TuningScore score, double precisionFloor)
{
    var missedRate = score.MissedChoruses / (double)Math.Max(1, score.ChorusCount);
    var feasibleLoss = 4 * (1 - score.Recall) + (1 - score.Precision) +
        1.2 * missedRate + .35 * score.BoundaryError + 4 * score.GroundedError;
    var deficit = Math.Max(0, precisionFloor - score.Precision);
    var groundedLeak = Math.Max(0, score.GroundedError);
    return feasibleLoss + (deficit > 0 || groundedLeak > 0
        ? 100 + 1_000 * deficit + 1_000 * groundedLeak
        : 0);
}

static TuningScore Score(IReadOnlyList<PreparedTrack> tracks, ChorusDetectionParameters parameters,
    bool includeTracks = false)
{
    double truePositive = 0, falsePositive = 0, falseNegative = 0, boundaryError = 0, groundedError = 0;
    var missedChoruses = 0;
    var chorusCount = 0;
    var trackScores = new List<object>();
    foreach (var track in tracks)
    {
        var input = track.Input;
        var predicted = JumpWindowPlanner.Plan(input.Envelope, .125, input.FullnessFloor, input.FullnessCeiling,
            input.DanceMetrics, input.SpectralFrames, parameters);
        var duration = Math.Max(1, (int)Math.Ceiling(input.Envelope.Count * .125));
        var expectedMask = Mask(duration, track.Corpus.Choruses);
        var predictedMask = Mask(duration, predicted);
        var trackTruePositive = Enumerable.Range(0, duration).Count(second => expectedMask[second] && predictedMask[second]);
        var trackFalsePositive = Enumerable.Range(0, duration).Count(second => !expectedMask[second] && predictedMask[second]);
        var trackFalseNegative = Enumerable.Range(0, duration).Count(second => expectedMask[second] && !predictedMask[second]);
        truePositive += trackTruePositive;
        falsePositive += trackFalsePositive;
        falseNegative += trackFalseNegative;
        if (track.Corpus.Choruses.Count == 0) groundedError += trackFalsePositive / (double)duration;
        foreach (var chorus in track.Corpus.Choruses)
        {
            chorusCount++;
            var overlaps = predicted.Select(window => (Window: window, Overlap: Overlap(window, chorus)))
                .OrderByDescending(candidate => candidate.Overlap).ToArray();
            if (overlaps.Length == 0 || overlaps[0].Overlap < Math.Min(5, chorus.DurationSeconds * .45))
            {
                missedChoruses++;
                boundaryError += 1;
                continue;
            }
            boundaryError += (Math.Min(15, Math.Abs(overlaps[0].Window.StartSeconds - chorus.StartSeconds)) +
                Math.Min(15, Math.Abs(overlaps[0].Window.EndSeconds - chorus.EndSeconds))) / 30;
        }
        if (includeTracks)
            trackScores.Add(new
            {
                track.Corpus.Title,
                expected = track.Corpus.Choruses,
                predicted,
                precision = trackTruePositive / (double)Math.Max(1, trackTruePositive + trackFalsePositive),
                recall = trackTruePositive / (double)Math.Max(1, trackTruePositive + trackFalseNegative)
            });
    }
    var precision = truePositive / Math.Max(1, truePositive + falsePositive);
    var recall = truePositive / Math.Max(1, truePositive + falseNegative);
    const double betaSquared = .55 * .55;
    var fScore = (1 + betaSquared) * precision * recall / Math.Max(1e-9, betaSquared * precision + recall);
    var missedRate = missedChoruses / (double)Math.Max(1, chorusCount);
    var normalizedBoundaryError = boundaryError / Math.Max(1, chorusCount);
    var diagnosticLoss = 7 * (1 - precision) + 1.7 * (1 - recall) + 1.1 * missedRate +
        .45 * normalizedBoundaryError + 3.5 * groundedError;
    return new TuningScore(diagnosticLoss, precision, recall, fScore, missedChoruses, chorusCount,
        normalizedBoundaryError, groundedError, includeTracks ? trackScores : null);
}

static bool[] Mask(int duration, IReadOnlyList<JumpWindow> windows)
{
    var mask = new bool[duration];
    foreach (var window in windows)
        for (var second = Math.Max(0, (int)Math.Floor(window.StartSeconds));
             second < Math.Min(duration, (int)Math.Ceiling(window.EndSeconds)); second++)
            mask[second] = true;
    return mask;
}

static double Overlap(JumpWindow left, JumpWindow right) =>
    Math.Max(0, Math.Min(left.EndSeconds, right.EndSeconds) - Math.Max(left.StartSeconds, right.StartSeconds));

sealed record CorpusTrack(string Id, string Title, IReadOnlyList<JumpWindow> Choruses);
sealed record PreparedTrack(CorpusTrack Corpus, MotionPlanningInput Input);
sealed record TuningScore(double Loss, double Precision, double Recall, double FScore, int MissedChoruses,
    int ChorusCount, double BoundaryError, double GroundedError, IReadOnlyList<object>? TrackScores);
