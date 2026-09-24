using LoreFetch.Core.Detection;
using OpenCvSharp;

namespace LoreFetch.Lab.Accuracy;

/// What fraction of the ground truth this run actually had fixture files
/// for, and which (height, layout, rung, mat) combinations that subset
/// covers -- printed alongside every report so a partial-corpus run can
/// never be read as the full result (docs/design/identification.md
/// Fallbacks: the same point made about a 5k index -- "label any such
/// table with its index size" -- applies just as much to a partial
/// fixture corpus).
public sealed record CorpusCoverage(
    int FramesInGroundTruth,
    int FramesFoundOnDisk,
    IReadOnlyList<double> HeightsCovered,
    IReadOnlyList<string> RungsCovered,
    IReadOnlyList<string> MatsCovered)
{
    public bool IsPartial => FramesFoundOnDisk < FramesInGroundTruth;

    public string Summarize() =>
        $"{FramesFoundOnDisk} of {FramesInGroundTruth} ground-truth frame(s) found on disk" +
        (FramesFoundOnDisk == 0
            ? " (none)."
            : $" -- heights: {string.Join(", ", HeightsCovered.Select(h => $"{h:0.##}in"))}; " +
              $"rungs: {string.Join(", ", RungsCovered)}; mats: {string.Join(", ", MatsCovered)}.");
}

/// Locates `test-images/ground-truth.csv` and `test-images/fixtures/`
/// relative to the repo root, resolves every frame's `ResolvedGroundTruthRow`s
/// against the loaded index, and splits frames into "found on disk" (the
/// harness actually runs these) vs. "missing" (H3's corpus is delivered in
/// batches -- DECISIONS.md's own "run on whatever subset exists" requirement).
/// Never writes to `ground-truth.csv` -- read-only, per this package's
/// brief.
public static class AccuracyCorpusLoader
{
    public const string GroundTruthRelativePath = "test-images/ground-truth.csv";

    public const string FixturesRelativeDir = "test-images/fixtures";

    /// `frame.File` is relative to `test-images/` (e.g.
    /// `fixtures/15in/9/...png`), matching `capture-fixtures.sh`'s own
    /// `CSV_FILE_FIELD`.
    public static string ResolveFixturePath(string repoRoot, string relativeFile) =>
        Path.Combine(repoRoot, "test-images", relativeFile.Replace('/', Path.DirectorySeparatorChar));

    /// Splits `allFrames` into those whose fixture file exists on disk and
    /// those that don't, and reports the coverage those two lists imply.
    public static (IReadOnlyList<GroundTruthFrame> Found, IReadOnlyList<GroundTruthFrame> Missing, CorpusCoverage Coverage)
        SplitByPresence(string repoRoot, IReadOnlyList<GroundTruthFrame> allFrames)
    {
        ArgumentNullException.ThrowIfNull(repoRoot);
        ArgumentNullException.ThrowIfNull(allFrames);

        var found = new List<GroundTruthFrame>();
        var missing = new List<GroundTruthFrame>();

        foreach (var frame in allFrames)
        {
            var path = ResolveFixturePath(repoRoot, frame.File);
            (File.Exists(path) ? found : missing).Add(frame);
        }

        var coverage = new CorpusCoverage(
            FramesInGroundTruth: allFrames.Count,
            FramesFoundOnDisk: found.Count,
            HeightsCovered: found.Select(f => f.HeightIn).Distinct().OrderBy(h => h).ToList(),
            RungsCovered: found.SelectMany(f => f.Slots).Select(s => s.Row.Rung).Distinct(StringComparer.Ordinal).OrderBy(r => r, StringComparer.Ordinal).ToList(),
            MatsCovered: found.SelectMany(f => f.Slots).Select(s => s.Row.Mat).Distinct(StringComparer.Ordinal).OrderBy(m => m, StringComparer.Ordinal).ToList());

        return (found, missing, coverage);
    }

    /// Loads a fixture PNG as a `CameraFrame` -- `Cv2.ImRead` (color) then
    /// `FrameMat.FromMat`, the same two calls `LoreFetch.Lab detect`/`identify`
    /// already use for a still image, so there is no second image-loading
    /// path to diverge from those commands' own.
    public static Mat LoadFixtureMat(string path)
    {
        var mat = Cv2.ImRead(path, ImreadModes.Color);
        if (mat.Empty())
        {
            mat.Dispose();
            throw new InvalidOperationException($"AccuracyCorpusLoader: could not decode fixture image \"{path}\".");
        }

        return mat;
    }
}
