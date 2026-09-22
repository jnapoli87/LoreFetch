using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Imaging;
using LoreFetch.Lab;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;
using Xunit;

namespace LoreFetch.Tests.StreamB;

/// Artifact-gated (package B5a's "real-capture check"): runs
/// `ContourCardDetector` against the 12 real C920 frames in
/// `test-images/ad-hoc/` -- {solring, verix_sleeved, atarka_foil, plains} x
/// {black, brown, white} mat, ~12" height, a cluttered desk. Skipped with a
/// reason (via `RealCaptureGate`, mirroring `Tests/Integration`'s
/// `RealArtifactGate` / orchestration finding V7) when the folder is
/// absent -- card imagery can never be committed, so this can only ever run
/// locally. Set `LOREFETCH_REQUIRE_REAL=1` to turn that skip into a
/// failure on a machine that DOES have the fixtures.
///
/// Expects exactly one detection per frame, EXCEPT the two documented,
/// individually-named misses below -- see each one's own comment. Nothing
/// here is a blanket "at least N of 12" check: weakening the assertion for
/// every frame to accommodate one hard case would hide a regression in the
/// other eleven, which is exactly the overfitting CLAUDE.md warns against
/// ("do not tune parameters until it passes by overfitting" for the
/// Risk-4 Plains-on-black case specifically).
public class ContourCardDetectorRealCaptureTests
{
    /// Atarka's Grandeur, foiled: glare defeats hashing (and, it turns out,
    /// detection) without polarized or diffuse light. CLAUDE.md Risk 6:
    /// "Foils. ... Out of v1 scope -- document the failure rather than
    /// hiding it." A miss here is informative, not fatal.
    private const string KnownMissFoilOnBlack = "atarka_foil_black.png";

    /// Verix Bladewing, sleeved, on the black mat: measured miss, recorded
    /// here rather than silently tuned away. The sleeve's glossy surface
    /// against the black foam produced enough spurious/fragmented
    /// contours (33 NotAQuad + 2 NonConvex + 1 MinArea rejections, zero
    /// accepted -- see the `detect --all` run this package's report
    /// includes) that no single contour survived to `approxPolyDP` as a
    /// clean convex quadrilateral. This is Risk 1 (glare/focus/tilt), not
    /// the Risk-4 mat-contrast case the Plains-on-black fixture already
    /// covers and passes -- and per CLAUDE.md's instruction for exactly
    /// this situation, it is recorded as a known miss rather than chased
    /// with parameters tuned to this one frame.
    private const string KnownMissSleevedOnBlack = "verix_sleeved_black.png";

    [Fact]
    public void Detect_AdHocFixtures_OneCardPerFrame_ExceptDocumentedMisses()
    {
        if (!RepoPaths.TryFindRepoRoot(out var repoRoot))
        {
            RealCaptureGate.SkipOrFail("could not locate the repository root to find test-images/ad-hoc/.");
            return;
        }

        var dir = Path.Combine(repoRoot!, "test-images", "ad-hoc");
        if (!Directory.Exists(dir))
        {
            RealCaptureGate.SkipOrFail($"real C920 fixtures not present at \"{dir}\" -- card imagery can never be committed.");
            return;
        }

        var files = Directory.EnumerateFiles(dir, "*.png").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0)
        {
            RealCaptureGate.SkipOrFail($"\"{dir}\" exists but contains no .png fixtures.");
            return;
        }

        var detector = new ContourCardDetector(NullLogger<ContourCardDetector>.Instance);
        var failures = new List<string>();
        var results = new List<string>();

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            using var color = Cv2.ImRead(file, ImreadModes.Color);
            using var frame = FrameMat.FromMat(color);

            var diagnostics = detector.DetectWithDiagnostics(frame, maxCards: 9);
            var isKnownMiss = name is KnownMissFoilOnBlack or KnownMissSleevedOnBlack;

            results.Add(diagnostics.Accepted.Count == 1
                ? $"{name}: accepted 1, {ApproxSize(diagnostics.Accepted[0])}"
                : $"{name}: accepted {diagnostics.Accepted.Count}{(isKnownMiss ? " (documented known miss)" : string.Empty)}, " +
                  $"rejects [{string.Join(", ", diagnostics.Rejected.GroupBy(r => r.Reason).Select(g => $"{g.Key}={g.Count()}"))}]");

            if (!isKnownMiss && diagnostics.Accepted.Count != 1)
            {
                failures.Add($"{name}: expected exactly 1 detection, got {diagnostics.Accepted.Count}");
            }

            if (isKnownMiss && diagnostics.Accepted.Count == 1)
            {
                // A documented miss that now passes is good news, not a
                // problem -- but it means the comment above is stale and
                // needs updating, so surface it loudly rather than let the
                // list quietly drift from reality.
                failures.Add($"{name}: documented as a known miss but detection now succeeds -- update the comment.");
            }
        }

        Assert.True(failures.Count == 0, "Real-capture check:\n" + string.Join('\n', results) + "\n\nFailures:\n" + string.Join('\n', failures));
    }

    private static string ApproxSize(CardQuad quad)
    {
        var width = (Distance(quad.TL, quad.TR) + Distance(quad.BL, quad.BR)) / 2f;
        var height = (Distance(quad.TL, quad.BL) + Distance(quad.TR, quad.BR)) / 2f;
        return $"{width:F0}x{height:F0}px";
    }

    private static float Distance(PointF2 a, PointF2 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }
}
