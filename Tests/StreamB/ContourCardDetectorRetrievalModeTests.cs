using LoreFetch.Core.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;
using Xunit;

namespace LoreFetch.Tests.StreamB;

/// Package B5-something (real-frame H1/H2/H3 investigation): a
/// `ContourDetectorOptions.RetrievalMode` knob was added so
/// `RetrievalModes.List` -- the alternative to CLAUDE.md's pinned, still-
/// default `RetrievalModes.External` -- can be measured against the real
/// `a_corpus` frames via `LoreFetch.Lab RetrievalExperimentCommand`. These
/// tests cover the flag itself: the default is unchanged, and `List`
/// actually does what it is supposed to on a synthetic nested frame --
/// surface MORE raw contours than `External` -- while
/// `DedupeAndTakeTopN`'s nested/duplicate suppression (B5a's own dead-code
/// finding under `External`) actually activates and collapses the extra
/// contours back down, rather than double-counting them.
public class ContourCardDetectorRetrievalModeTests
{
    private const int FrameWidth = 640;
    private const int FrameHeight = 480;

    [Fact]
    public void ContourDetectorOptions_Default_RetrievalModeIsExternal()
    {
        // CLAUDE.md "Card detection" pins RETR_EXTERNAL -- a settled
        // decision this package does not renegotiate. The new knob must
        // not change the shipped default.
        Assert.Equal(RetrievalModes.External, ContourDetectorOptions.Default.RetrievalMode);
    }

    [Fact]
    public void Detect_NestedInnerFrameBand_RetrievalModeList_FindsMoreRawContoursThanExternal()
    {
        // DetectorTestFrames.CardOnMat draws a full card -- outer border,
        // inner frame band, art box -- which is exactly the nested-quad
        // shape B5a's own comment describes: RETR_EXTERNAL structurally
        // never returns the inner contours, so DedupeAndTakeTopN's nested-
        // duplicate branch is dead code under it. RETR_LIST must surface
        // them.
        var (frame, _) = DetectorTestFrames.CardOnMat(
            FrameWidth, FrameHeight, FrameWidth / 2f, FrameHeight / 2f, shortSidePx: 200, angleDegrees: 0);
        using var f = frame;

        var externalDetector = new ContourCardDetector(ContourDetectorOptions.Default, NullLogger<ContourCardDetector>.Instance);
        var listOptions = ContourDetectorOptions.Default with { RetrievalMode = RetrievalModes.List };
        var listDetector = new ContourCardDetector(listOptions, NullLogger<ContourCardDetector>.Instance);

        var externalDiagnostics = externalDetector.DetectWithDiagnostics(f, maxCards: 9);
        var listDiagnostics = listDetector.DetectWithDiagnostics(f, maxCards: 9);

        // Every contour RunPipeline looks at ends up in EXACTLY one of
        // Accepted or Rejected -- see ContourCardDetector.RunPipeline's own
        // loop -- so Accepted.Count + Rejected.Count is the total raw
        // contour count findContours returned, without needing a separate
        // internal seam just to count them.
        var externalContourCount = externalDiagnostics.Accepted.Count + externalDiagnostics.Rejected.Count;
        var listContourCount = listDiagnostics.Accepted.Count + listDiagnostics.Rejected.Count;

        Assert.True(
            listContourCount > externalContourCount,
            $"Expected RETR_LIST to surface more raw contours than RETR_EXTERNAL on a nested frame " +
            $"(external={externalContourCount}, list={listContourCount}).");
    }

    [Fact]
    public void Detect_NestedInnerFrameBand_RetrievalModeList_NestedDedupeActivatesAndSuppressesInnerQuad()
    {
        var (frame, _) = DetectorTestFrames.CardOnMat(
            FrameWidth, FrameHeight, FrameWidth / 2f, FrameHeight / 2f, shortSidePx: 200, angleDegrees: 0);
        using var f = frame;

        var externalDetector = new ContourCardDetector(ContourDetectorOptions.Default, NullLogger<ContourCardDetector>.Instance);
        var listOptions = ContourDetectorOptions.Default with { RetrievalMode = RetrievalModes.List };
        var listDetector = new ContourCardDetector(listOptions, NullLogger<ContourCardDetector>.Instance);

        var externalDiagnostics = externalDetector.DetectWithDiagnostics(f, maxCards: 9);
        var listDiagnostics = listDetector.DetectWithDiagnostics(f, maxCards: 9);

        // B5a's own comment: under External, the inner frame band never
        // reaches findContours' output, so NestedDuplicate never fires --
        // reconfirmed here as the control.
        Assert.DoesNotContain(externalDiagnostics.Rejected, r => r.Reason == ContourRejectReason.NestedDuplicate);

        // Under List, the inner frame band DOES reach the candidate list --
        // proven by NestedDuplicate actually firing -- and DedupeAndTakeTopN
        // suppresses it, so the card is still counted exactly once, not
        // twice. This is package B5a's dead-code finding actually being
        // exercised end-to-end for the first time.
        Assert.Contains(listDiagnostics.Rejected, r => r.Reason == ContourRejectReason.NestedDuplicate);
        Assert.Single(listDiagnostics.Accepted);
    }
}
