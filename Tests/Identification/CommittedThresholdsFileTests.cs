using LoreFetch.Core.Scanning;
using LoreFetch.Lab;
using Xunit;

namespace LoreFetch.Tests.Identification;

/// Package B6-thresholds: the committed `data/index/thresholds.json` must
/// itself load cleanly through `ThresholdsFile.Load` (Core/Scanning, frozen
/// contract surface) -- not gated on any external cache, since the file is
/// committed and this is just a read of a tracked file. This is the sanity
/// check that closes the loop `RoundTripThresholdsDocument`'s own doc
/// comment warns about: that type deliberately omits `goodDistance`/
/// `okDistance`, so `ThresholdsFile.Load` throws "missing required field"
/// until B6 adds them -- this test asserts B6 DID add them, and that
/// everything else `ThresholdsFile` requires survived the round trip.
public class CommittedThresholdsFileTests
{
    [Fact]
    public void Load_CommittedThresholdsFile_SucceedsAndCarriesTheCalibratedDistances()
    {
        Assert.True(RepoPaths.TryFindRepoRoot(out var repoRoot), "could not locate the repository root.");

        var path = Path.Combine(repoRoot!, "data", "index", "thresholds.json");
        Assert.True(File.Exists(path), $"committed thresholds file not present at \"{path}\".");

        var thresholds = ThresholdsFile.Load(path);

        Assert.Equal(ThresholdsFile.SupportedFormatVersion, thresholds.FormatVersion);
        Assert.False(string.IsNullOrWhiteSpace(thresholds.IndexSha256));
        Assert.True(thresholds.IndexArtworkCount > 0);
        Assert.False(string.IsNullOrWhiteSpace(thresholds.Notes));

        // The sanity relationship DECISIONS.md/orchestration-plan.md expect
        // between the three distances, even though ThresholdsFile.Load
        // itself does not enforce it (no code currently does -- see this
        // package's own report): a "good" match must never require MORE
        // distance than an "ok" one, and the round-trip reference floor
        // (render-vs-itself) must sit strictly below the real-photo
        // "good" threshold, since a webcam photo is never as clean as a
        // render matching its own index entry.
        Assert.True(
            thresholds.GoodDistance <= thresholds.OkDistance,
            $"goodDistance ({thresholds.GoodDistance}) must be <= okDistance ({thresholds.OkDistance}).");
        Assert.True(
            thresholds.ReferenceFloor < thresholds.GoodDistance,
            $"referenceFloor ({thresholds.ReferenceFloor}) must be < goodDistance ({thresholds.GoodDistance}).");

        // Pinned to this session's committed calibration (docs/accuracy.md
        // "Results -- win-x64 committed index rebuild"): goodDistance is
        // the max observed correct rank-1 distance (208) from the 54-slot
        // H3 corpus; okDistance (240) is the midpoint of the empty
        // 209-271 window between that and the lowest observed wrong
        // rank-1 distance (272). A change to either value is a deliberate
        // recalibration, not something that should drift silently -- if
        // this assertion ever needs to move, update it alongside a fresh
        // docs/accuracy.md entry, not instead of one.
        Assert.Equal(208, thresholds.GoodDistance);
        Assert.Equal(240, thresholds.OkDistance);
        Assert.False(thresholds.Notes.Contains("NOT YET SET", StringComparison.Ordinal));
        Assert.False(thresholds.Notes.Contains("PROVISIONAL", StringComparison.Ordinal));
    }
}
