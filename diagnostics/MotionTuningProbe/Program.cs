using System.Diagnostics;
using System.Text.Json;
using InnerTune;

var populationSize = ReadOption("--population", 18, 8, 80);
var generations = ReadOption("--generations", 10, 1, 100);
var useGpu = args.Contains("--gpu", StringComparer.OrdinalIgnoreCase);
var gpuPython = ReadTextOption("--gpu-python") ?? "python3";
var wslUser = ReadTextOption("--wsl-user");
var cacheDirectory = ReadTextOption("--cache") ??
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InnerTune", "audio-cache");
var corpus = new[]
{
    Labeled("vIOO_7DLr3M", "BURN IT DOWN", (68, 90), (129, 150), (173, 211)),
    Labeled("KFA1hM91ffo", "In the End", (54, 74), (109, 130), (165, 186)),
    ValidationLabeled("8ZBnwBVjwOk", "New Divide", (67, 104), (139, 161), (196, 250)),
    Labeled("KwN_f0fTHoE", "One Step Closer", (43, 55), (78, 100), (131, 155)),
    ValidationLabeled("yOW7Eh81Gto", "Faint", (52, 68), (91, 107), (121, 161)),
    ValidationLabeled("eXRWHzV72sU", "What I've Done", (64, 85), (104, 122), (162, 180)),
    Labeled("_e7bqZGPyFI", "Breaking the Habit", (47, 73), (105, 130), (148, 180)),
    Labeled("PIidgyl8U9s", "Bleed It Out", (37, 52), (78, 93), (116, 144)),
    Labeled("dxp9w9Ggehc", "Numb", (52, 70), (104, 122), (141, 176)),
    Labeled("gx-fg-dMROg", "Crawling", (24, 42), (88, 106), (145, 202)),
    Labeled("-YQ8IbVIwPM", "Somewhere I Belong", (68, 92), (115, 139), (167, 212)),
    ValidationLabeled("6xzN8Nt0Pok", "I Wanna Dance with Somebody", (62, 96), (129, 163), (192, 286)),
    Labeled("kyzIQKuSqBs", "Blue (Da Ba Dee)", (34, 64), (112, 140), (158, 188)),
    ValidationLabeled("dEOmR6b0IqM", "What Is Love", (54, 85), (116, 148), (201, 231), (232, 268)),
    ValidationLabeled("zlJ0Aj9y67c", "Lose Yourself", (99, 122), (166, 189), (256, 279)),
    Labeled("-grPV-Fae6I", "Not Afraid", (67, 89), (134, 158), (224, 246)),
    Labeled("r5MR7_INQwg", "The Real Slim Shady", (83, 101), (147, 165), (211, 248)),
    Labeled("tqxRidAWER8", "Without Me", (90, 108), (150, 168), (219, 237)),
    Labeled("Obim8BYGnOE", "Till I Collapse", (101, 123), (168, 190), (235, 258)),
    Labeled("LhOjN4t0YsY", "Breathe", (60, 90), (123, 154), (182, 212), (300, 328)),
    Labeled("Hb9hvRSEel8", "Firestarter", (65, 123), (167, 185)),
    Labeled("y_-SP55sRig", "Pump Up The Jam", (15, 56), (61, 105), (171, 185), (292, 316)),
    ValidationLabeled("2zMIddjFAIA", "Pokémon Theme", (32, 65), (94, 128), (163, 193)),
    Labeled("x2umzqh8r4g", "CASTLE OF GLASS", (71, 89), (116, 134), (151, 187)),
    ValidationLabeled("uEITghr7Rxg", "Heavy", (28, 54), (79, 105), (124, 151)),
    Labeled("CWeZTY7Rc7w", "A Place for My Head", (50, 65), (94, 108), (137, 154)),
    Labeled("F6JPc0779Ys", "Hot In Herre", (66, 84), (120, 138), (174, 210)),
    Labeled("T_OWvLDIyno", "Swimming Pools (Drank)", (52, 91), (117, 156), (181, 221)),
    Labeled("w3LGyvzv7yg", "Antidote", (7, 36), (80, 143), (234, 255)),
    Grounded("5w3rRFWzjcM", "Sweden"),
    Grounded("gLgUesz8444", "An Ending"),
    ValidationGrounded("Jd8w8iPWGM8", "Home (Music Box)"),
    Grounded("AvDrW4JTjME", "Fallen Down (Reprise)"),
    Grounded("sJhnVunhNZY", "Dire Dire Docks"),
    Grounded("K892qn3U524", "Ezio's Family"),
    ValidationGrounded("H1LdQntDnFY", "One More Light"),
    ValidationGrounded("Lp-TdtDYGAA", "At Doom's Gate"),
    Grounded("813-3iL5OsE", "Dragonborn"),
    Grounded("1RVAJ2ZPTFQ", "Ocarina Of Time"),
    Grounded("EIaLT43HX9o", "The Legend Of Zelda - Main Theme"),
    ValidationGrounded("gt1pKrwxAJU", "Opus")
};

Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
var backendInitialization = Stopwatch.StartNew();
using var gpuAccelerator = useGpu ? new PythonGpuSimilarityAccelerator(gpuPython, wslUser) : null;
backendInitialization.Stop();
var prepared = new List<PreparedTrack>();
var contextPreparation = Stopwatch.StartNew();
var contextPreparationMilliseconds = 0L;
foreach (var item in corpus)
{
    var path = Path.Combine(cacheDirectory, $"{item.Id}.playable.m4a");
    if (!File.Exists(path)) continue;
    var input = await RepresentativeTempoAnalyzer.PrepareMotionPlanningAsync(path);
    if (input is not null)
    {
        var contextStart = contextPreparation.ElapsedMilliseconds;
        var context = JumpWindowPlanner.PrepareContext(input.Envelope, .125, input.FullnessFloor,
            input.FullnessCeiling, input.SpectralFrames, gpuAccelerator);
        contextPreparationMilliseconds += contextPreparation.ElapsedMilliseconds - contextStart;
        prepared.Add(new PreparedTrack(item, input, context));
    }
}
contextPreparation.Stop();
Console.Error.WriteLine($"similarity backend: {(useGpu ? gpuAccelerator!.Name : "CPU")}; " +
    $"context preparation={contextPreparationMilliseconds / 1000d:F3}s; " +
    $"audio+context preparation={contextPreparation.Elapsed.TotalSeconds:F3}s");
if (prepared.Count < 30) throw new InvalidOperationException($"Only {prepared.Count} labeled tracks could be prepared.");
var training = prepared.Where(track => !track.Corpus.Validation).ToArray();
var validation = prepared.Where(track => track.Corpus.Validation).ToArray();

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
    (10, 28),
    (18, 30),
    (.45, 1.10),
    (.25, .65),
    (.45, .85),
    (.20, .60),
    (.15, .50),
    (.08, .35),
    (.15, .45),
    (.08, .35)
};
var random = new Random(0x1A2B3C);
var defaultVector = ToVector(ChorusDetectionParameters.Default);
var styleSeeds = SolveStyleSeeds(prepared, bounds, defaultVector, random, Math.Min(12, populationSize - 1));
var population = Enumerable.Range(0, populationSize)
    .Select(index => index == 0
        ? defaultVector
        : index <= styleSeeds.Length
            ? styleSeeds[index - 1]
        : index <= populationSize / 2
            ? bounds.Select((bound, dimension) => Math.Clamp(
                defaultVector[dimension] + (random.NextDouble() * 2 - 1) * (bound.High - bound.Low) * .20,
                bound.Low, bound.High)).ToArray()
            : bounds.Select(bound => bound.Low + random.NextDouble() * (bound.High - bound.Low)).ToArray())
    .ToArray();
var scores = population.Select(vector => Score(prepared, ToParameters(vector))).ToArray();
var defaultScore = scores[0];
var defaultTrainingScore = Score(training, ChorusDetectionParameters.Default);
var defaultValidationScore = Score(validation, ChorusDetectionParameters.Default);
var precisionFloor = Math.Max(.699, defaultScore.Precision);

for (var generation = 0; generation < generations; generation++)
{
    // Keep the production preset as an immutable elite so the validation
    // gate always has a non-regressing fallback.
    for (var target = 1; target < population.Length; target++)
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
    Console.Error.WriteLine($"generation {generation + 1}/{generations}: objective={OptimizationLoss(best, precisionFloor):F5}, precision={best.Precision:P1}, recall={best.Recall:P1}, weak={best.LowCoverageChoruses}, gap={best.MeanLongestGapFraction:P1}");
}

var validationScores = population.Select(vector => Score(validation, ToParameters(vector))).ToArray();
var bestIndex = Enumerable.Range(0, scores.Length)
    .Where(index => IsFeasible(scores[index], precisionFloor) &&
        IsAuditAcceptable(validationScores[index], defaultValidationScore))
    .MinBy(index => OptimizationLoss(scores[index], precisionFloor));
var bestParameters = ToParameters(population[bestIndex]);
var bestTrainingScore = Score(training, bestParameters, true);
var bestValidationScore = Score(validation, bestParameters, true);
var defaultAllScore = Score(prepared, ChorusDetectionParameters.Default);
var bestAllScore = Score(prepared, bestParameters, true);
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
    trainingTracks = training.Length,
    validationTracks = validation.Length,
    generations,
    populationSize,
    similarityBackend = useGpu ? gpuAccelerator!.Name : "CPU",
    backendInitializationMilliseconds = backendInitialization.ElapsedMilliseconds,
    contextPreparationMilliseconds,
    audioAndContextPreparationMilliseconds = contextPreparation.ElapsedMilliseconds,
    precisionFloor,
    defaultTrainingScore,
    bestTrainingScore,
    defaultValidationScore,
    bestValidationScore,
    defaultAllScore = defaultScore,
    bestAllScore,
    objectiveImprovement = OptimizationLoss(defaultScore, precisionFloor) -
        OptimizationLoss(bestAllScore, precisionFloor),
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
    new(id, title, choruses.Select(chorus => new JumpWindow(chorus.Start, chorus.End)).ToArray(), false);

static CorpusTrack ValidationLabeled(string id, string title, params (double Start, double End)[] choruses) =>
    new(id, title, choruses.Select(chorus => new JumpWindow(chorus.Start, chorus.End)).ToArray(), true);

static CorpusTrack Grounded(string id, string title) => new(id, title, [], false);

static CorpusTrack ValidationGrounded(string id, string title) => new(id, title, [], true);

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
    parameters.BackwardSupportTolerance,
    parameters.MaximumBackwardSearchSeconds,
    parameters.MaximumRepeatedRunSeconds,
    parameters.RepeatedPartnerEnergyRatio,
    parameters.DenseRhythmicEnergyThreshold,
    parameters.SustainedEnergyThreshold,
    parameters.RhythmicPulseThreshold,
    parameters.BassRhythmThreshold,
    parameters.OnsetPulseThreshold,
    parameters.PercussivePulseThreshold,
    parameters.TransientStrengthThreshold
];

static ChorusDetectionParameters ToParameters(IReadOnlyList<double> vector) => new(
    vector[0], vector[1], vector[2], vector[3], vector[4], vector[5], vector[6], vector[7], vector[8], vector[9],
    (int)Math.Round(vector[10]), (int)Math.Round(vector[11]), vector[12], vector[13], vector[14], vector[15],
    vector[16], vector[17], vector[18], vector[19]);

static double[][] SolveStyleSeeds(IReadOnlyList<PreparedTrack> tracks,
    IReadOnlyList<(double Low, double High)> bounds, IReadOnlyList<double> defaultVector,
    Random random, int count)
{
    var best = new List<StyleCandidate>();
    for (var sample = 0; sample < 100_000; sample++)
    {
        var vector = defaultVector.ToArray();
        if (sample > 0)
            for (var dimension = 13; dimension < bounds.Count; dimension++)
                vector[dimension] = bounds[dimension].Low +
                    random.NextDouble() * (bounds[dimension].High - bounds[dimension].Low);
        var parameters = ToParameters(vector);
        var falsePositives = tracks.Count(track => track.Corpus.Choruses.Count == 0 &&
            JumpWindowPlanner.IsJumpWorthy(track.Input.DanceMetrics, parameters));
        var falseNegatives = tracks.Count(track => track.Corpus.Choruses.Count > 0 &&
            !JumpWindowPlanner.IsJumpWorthy(track.Input.DanceMetrics, parameters));
        var distance = Enumerable.Range(13, bounds.Count - 13)
            .Sum(dimension => Math.Abs(vector[dimension] - defaultVector[dimension]) /
                (bounds[dimension].High - bounds[dimension].Low));
        var candidate = new StyleCandidate(vector, falsePositives, falseNegatives, distance);
        if (best.Count >= 256 && CompareStyle(candidate, best[^1]) >= 0) continue;
        best.Add(candidate);
        best.Sort(CompareStyle);
        if (best.Count > 256) best.RemoveAt(best.Count - 1);
    }
    var seeds = best.Take(count).Select(candidate => candidate.Vector).ToArray();
    if (best.Count > 0)
        Console.Error.WriteLine($"style seed: false-positive={best[0].FalsePositives}, false-negative={best[0].FalseNegatives}");
    return seeds;
}

static int CompareStyle(StyleCandidate left, StyleCandidate right)
{
    var comparison = left.FalsePositives.CompareTo(right.FalsePositives);
    if (comparison != 0) return comparison;
    comparison = left.FalseNegatives.CompareTo(right.FalseNegatives);
    return comparison != 0 ? comparison : left.Distance.CompareTo(right.Distance);
}

static double OptimizationLoss(TuningScore score, double precisionFloor)
{
    var missedRate = score.MissedChoruses / (double)Math.Max(1, score.ChorusCount);
    var weakRate = score.LowCoverageChoruses / (double)Math.Max(1, score.ChorusCount);
    var lowPrecisionTrackRate = score.LowPrecisionTracks / (double)Math.Max(1, score.PositiveTrackCount);
    var feasibleLoss = 2.2 * (1 - score.Recall) + .8 * (1 - score.Precision) +
        3 * (1 - score.MeanChorusCoverage) + 4.5 * score.MeanLongestGapFraction +
        2 * weakRate + 1.2 * missedRate + .25 * score.BoundaryError + 4 * score.GroundedError +
        2 * (1 - score.MeanTrackPrecision) + 1.5 * lowPrecisionTrackRate;
    var deficit = Math.Max(0, precisionFloor - score.Precision);
    var groundedLeak = Math.Max(0, score.GroundedError);
    var styleFalsePositiveExcess = Math.Max(0, score.StyleFalsePositives - 1);
    return feasibleLoss + (deficit > 0 || groundedLeak > 0 || score.StyleFalseNegatives > 0 ||
        styleFalsePositiveExcess > 0
        ? 100 + 1_000 * deficit + 1_000 * groundedLeak + 100 * score.StyleFalseNegatives +
            25 * styleFalsePositiveExcess
        : 0);
}

static bool IsFeasible(TuningScore candidate, double precisionFloor) =>
    candidate.Precision >= precisionFloor && candidate.GroundedError == 0 &&
    candidate.StyleFalseNegatives == 0 && candidate.StyleFalsePositives <= 1;

static bool IsAuditAcceptable(TuningScore candidate, TuningScore baseline) =>
    candidate.MeanLongestGapFraction <= baseline.MeanLongestGapFraction &&
    candidate.LowCoverageChoruses <= baseline.LowCoverageChoruses &&
    candidate.GroundedError == 0;

static TuningScore Score(IReadOnlyList<PreparedTrack> tracks, ChorusDetectionParameters parameters,
    bool includeTracks = false)
{
    double truePositive = 0, falsePositive = 0, falseNegative = 0, boundaryError = 0, groundedError = 0;
    double chorusCoverageTotal = 0, longestGapFractionTotal = 0;
    var missedChoruses = 0;
    var lowCoverageChoruses = 0;
    var chorusCount = 0;
    var styleFalsePositives = 0;
    var styleFalseNegatives = 0;
    var positiveTrackCount = 0;
    var lowPrecisionTracks = 0;
    double trackPrecisionTotal = 0;
    var trackScores = new List<object>();
    foreach (var track in tracks)
    {
        var input = track.Input;
        var jumpWorthy = JumpWindowPlanner.IsJumpWorthy(input.DanceMetrics, parameters);
        if (track.Corpus.Choruses.Count == 0 && jumpWorthy) styleFalsePositives++;
        if (track.Corpus.Choruses.Count > 0 && !jumpWorthy) styleFalseNegatives++;
        var predicted = JumpWindowPlanner.Plan(input.Envelope, .125, input.FullnessFloor, input.FullnessCeiling,
            input.DanceMetrics, input.SpectralFrames, parameters, track.Context);
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
        else
        {
            positiveTrackCount++;
            var trackPrecision = trackTruePositive /
                (double)Math.Max(1, trackTruePositive + trackFalsePositive);
            trackPrecisionTotal += trackPrecision;
            if (trackPrecision < .60) lowPrecisionTracks++;
        }
        foreach (var chorus in track.Corpus.Choruses)
        {
            chorusCount++;
            var chorusQuality = MeasureChorus(predictedMask, chorus);
            chorusCoverageTotal += chorusQuality.Coverage;
            longestGapFractionTotal += chorusQuality.LongestGapFraction;
            if (chorusQuality.Coverage < .70) lowCoverageChoruses++;
            var overlaps = predicted.Select(window => (Window: window, Overlap: Overlap(window, chorus)))
                .OrderByDescending(candidate => candidate.Overlap).ToArray();
            if (chorusQuality.Coverage < .45 || overlaps.Length == 0 ||
                overlaps[0].Overlap < Math.Min(5, chorus.DurationSeconds * .45))
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
                recall = trackTruePositive / (double)Math.Max(1, trackTruePositive + trackFalseNegative),
                chorusQuality = track.Corpus.Choruses.Select(chorus => MeasureChorus(predictedMask, chorus))
            });
    }
    var precision = truePositive / Math.Max(1, truePositive + falsePositive);
    var recall = truePositive / Math.Max(1, truePositive + falseNegative);
    const double betaSquared = .55 * .55;
    var fScore = (1 + betaSquared) * precision * recall / Math.Max(1e-9, betaSquared * precision + recall);
    var missedRate = missedChoruses / (double)Math.Max(1, chorusCount);
    var normalizedBoundaryError = boundaryError / Math.Max(1, chorusCount);
    var meanChorusCoverage = chorusCoverageTotal / Math.Max(1, chorusCount);
    var meanLongestGapFraction = longestGapFractionTotal / Math.Max(1, chorusCount);
    var meanTrackPrecision = trackPrecisionTotal / Math.Max(1, positiveTrackCount);
    var diagnosticLoss = 7 * (1 - precision) + 1.7 * (1 - recall) + 1.1 * missedRate +
        .45 * normalizedBoundaryError + 3.5 * groundedError +
        2 * (1 - meanChorusCoverage) + 3 * meanLongestGapFraction +
        2 * (1 - meanTrackPrecision) + lowPrecisionTracks / (double)Math.Max(1, positiveTrackCount);
    return new TuningScore(diagnosticLoss, precision, recall, fScore, missedChoruses, chorusCount,
        lowCoverageChoruses, meanChorusCoverage, meanLongestGapFraction,
        normalizedBoundaryError, groundedError, styleFalsePositives, styleFalseNegatives,
        meanTrackPrecision, lowPrecisionTracks, positiveTrackCount,
        includeTracks ? trackScores : null);
}

static ChorusQuality MeasureChorus(IReadOnlyList<bool> predictedMask, JumpWindow chorus)
{
    var start = Math.Clamp((int)Math.Floor(chorus.StartSeconds), 0, predictedMask.Count);
    var end = Math.Clamp((int)Math.Ceiling(chorus.EndSeconds), start, predictedMask.Count);
    var duration = Math.Max(1, end - start);
    var covered = 0;
    var gap = 0;
    var longestGap = 0;
    for (var second = start; second < end; second++)
    {
        if (predictedMask[second])
        {
            covered++;
            gap = 0;
        }
        else
        {
            gap++;
            longestGap = Math.Max(longestGap, gap);
        }
    }
    return new ChorusQuality(covered / (double)duration, longestGap / (double)duration, longestGap);
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

sealed record CorpusTrack(string Id, string Title, IReadOnlyList<JumpWindow> Choruses, bool Validation);
sealed record PreparedTrack(CorpusTrack Corpus, MotionPlanningInput Input, JumpPlanningContext? Context);
sealed record ChorusQuality(double Coverage, double LongestGapFraction, int LongestGapSeconds);
sealed record StyleCandidate(double[] Vector, int FalsePositives, int FalseNegatives, double Distance);
sealed record TuningScore(double Loss, double Precision, double Recall, double FScore, int MissedChoruses,
    int ChorusCount, int LowCoverageChoruses, double MeanChorusCoverage, double MeanLongestGapFraction,
    double BoundaryError, double GroundedError, int StyleFalsePositives, int StyleFalseNegatives,
    double MeanTrackPrecision, int LowPrecisionTracks, int PositiveTrackCount,
    IReadOnlyList<object>? TrackScores);

sealed class PythonGpuSimilarityAccelerator : IJumpSimilarityAccelerator, IDisposable
{
    private readonly Process process;
    private readonly BinaryWriter writer;
    private readonly BinaryReader reader;
    private readonly List<string> errors = [];

    public PythonGpuSimilarityAccelerator(string pythonExecutable, string? wslUser)
    {
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "gpu_similarity_worker.py");
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "wsl.exe"),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (!string.IsNullOrWhiteSpace(wslUser))
        {
            startInfo.ArgumentList.Add("--user");
            startInfo.ArgumentList.Add(wslUser);
        }
        startInfo.ArgumentList.Add(pythonExecutable);
        startInfo.ArgumentList.Add(ToWslPath(scriptPath));
        process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the CUDA worker.");
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data)) errors.Add(eventArgs.Data);
        };
        process.BeginErrorReadLine();
        writer = new BinaryWriter(process.StandardInput.BaseStream);
        reader = new BinaryReader(process.StandardOutput.BaseStream);
        int nameLength;
        try
        {
            nameLength = reader.ReadInt32();
        }
        catch (EndOfStreamException exception)
        {
            process.WaitForExit(5_000);
            throw new InvalidOperationException("The Python CUDA worker failed to initialize. " +
                string.Join(Environment.NewLine, errors), exception);
        }
        Name = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(nameLength));
    }

    public string Name { get; }

    public double[] MeasurePairwiseSimilarities(IReadOnlyList<double[]> features)
    {
        if (features.Count == 0) return [];
        var bandCount = features.Min(row => row.Length);
        writer.Write(features.Count);
        writer.Write(bandCount);
        for (var row = 0; row < features.Count; row++)
            for (var band = 0; band < bandCount; band++) writer.Write((float)features[row][band]);
        writer.Flush();

        var outputLength = reader.ReadInt32();
        if (outputLength != features.Count * features.Count)
            throw new InvalidOperationException("The CUDA worker returned an invalid matrix.");
        var result = new double[outputLength];
        for (var index = 0; index < result.Length; index++) result[index] = reader.ReadSingle();
        return result;
    }

    public void Dispose()
    {
        writer.Write(0);
        writer.Write(0);
        writer.Flush();
        process.StandardInput.Close();
        if (!process.WaitForExit(5_000)) process.Kill(entireProcessTree: true);
        reader.Dispose();
        writer.Dispose();
        process.Dispose();
        if (errors.Count > 0) Console.Error.WriteLine(string.Join(Environment.NewLine, errors));
    }

    private static string ToWslPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (fullPath.Length < 3 || fullPath[1] != ':')
            throw new InvalidOperationException($"The CUDA worker must be staged on a Windows drive: {fullPath}");
        var drive = char.ToLowerInvariant(fullPath[0]);
        return $"/mnt/{drive}/{fullPath[3..].Replace('\\', '/')}";
    }
}
