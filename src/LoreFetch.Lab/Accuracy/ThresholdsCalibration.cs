using System.Text.Json.Nodes;

namespace LoreFetch.Lab.Accuracy;

/// The code that WOULD calibrate and write `goodDistance`/`okDistance` into
/// `data/index/thresholds.json` -- built, because B6's brief asks for it to
/// exist, but deliberately never called anywhere in this package's own CLI
/// command against synthetic data, and never invoked by any test. Those two
/// values must be calibrated from the REAL H3 fixture corpus
/// (`AccuracyHarnessOptions`'s own doc comment; CLAUDE.md's Machine-split
/// rule 4), which does not exist yet -- writing them from a synthetic self-
/// test run would silently plant a fabricated threshold that reads as a
/// real measurement. `Suggest` is exposed publicly (and unit-tested) so its
/// arithmetic is provably correct; `Write` exists only for whoever runs
/// this against the real corpus later, gated behind `lab accuracy`'s own
/// explicit `--write-thresholds` flag (never passed by default, and not
/// passed in this session).
public static class ThresholdsCalibration
{
    /// `goodDistance`: the largest own-distance among the headline's own
    /// CORRECT slots -- every true match this run saw was at or below this,
    /// mirroring how B2 derives `referenceFloor` from its own correct
    /// population's own-distance max.
    ///
    /// `okDistance`: one less than the smallest rank-1 distance among the
    /// headline's own WRONG slots -- the tightest bound that would already
    /// have kept every confident-wrong answer this run actually observed
    /// out of the "confident" band. Falls back to
    /// `AccuracyHarnessOptions.OkDistance`'s own documented CardSpotter
    /// prior (270) when no wrong slot was observed to calibrate against --
    /// there is nothing else to derive it from in that case.
    ///
    /// Both are a SUGGESTION for a human to review against the run's own
    /// margin distribution, not an authoritative measurement -- see this
    /// type's own doc comment for why nothing in this package calls this
    /// automatically.
    public static (int GoodDistance, int OkDistance) Suggest(
        IReadOnlyList<SlotAccuracyResult> headlineResults, AccuracyHarnessOptions options)
    {
        ArgumentNullException.ThrowIfNull(headlineResults);
        ArgumentNullException.ThrowIfNull(options);

        var correctDistances = headlineResults
            .Where(r => r.Outcome == SlotOutcome.Correct && r.Rank1Distance.HasValue)
            .Select(r => r.Rank1Distance!.Value)
            .ToList();
        var wrongDistances = headlineResults
            .Where(r => r.Outcome == SlotOutcome.Wrong && r.Rank1Distance.HasValue)
            .Select(r => r.Rank1Distance!.Value)
            .ToList();

        var goodDistance = correctDistances.Count > 0 ? correctDistances.Max() : 0;
        var okDistance = wrongDistances.Count > 0 ? wrongDistances.Min() - 1 : options.OkDistance;

        return (goodDistance, okDistance);
    }

    /// Merges `goodDistance`/`okDistance` (plus B6's own provenance fields,
    /// following the same shape B2's `RoundTripThresholdsDocument` used for
    /// its own fields) into the EXISTING `thresholdsPath` JSON, preserving
    /// every field already there (B2's `referenceFloor`, `indexSha256`,
    /// margin provenance, etc.) rather than overwriting the whole file --
    /// `ThresholdsFile.Load`'s required fields are a strict subset of what
    /// this writes. Atomic write, same pattern as
    /// `RoundTripThresholdsWriter.Write`: temp file in the SAME directory,
    /// then `File.Move(overwrite: true)`.
    public static void Write(string thresholdsPath, int goodDistance, int okDistance, string measuredOnDetail, string notes)
    {
        ArgumentNullException.ThrowIfNull(thresholdsPath);
        ArgumentNullException.ThrowIfNull(measuredOnDetail);
        ArgumentNullException.ThrowIfNull(notes);

        var existingJson = File.Exists(thresholdsPath) ? File.ReadAllText(thresholdsPath) : "{}";
        var node = JsonNode.Parse(existingJson)?.AsObject() ?? new JsonObject();

        node["goodDistance"] = goodDistance;
        node["okDistance"] = okDistance;
        node["goodOkMeasuredOnDetail"] = measuredOnDetail;
        node["goodOkNotes"] = notes;

        var json = node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

        var directory = Path.GetDirectoryName(Path.GetFullPath(thresholdsPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = thresholdsPath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, thresholdsPath, overwrite: true);
    }
}
