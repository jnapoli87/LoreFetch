using System.Runtime.InteropServices;
using LoreFetch.Core.Abstractions;
using OpenCvSharp;

namespace LoreFetch.Lab.RoundTrip;

/// Wraps an already-decoded, canonical-size (488x680) BGR `Mat` -- a
/// Scryfall `normal` render, exactly `RectifiedCard.CanonicalWidth/Height`
/// (CLAUDE.md Scryfall: "`normal` is exactly `RectifiedCard`'s canonical
/// size, which is the whole reason 488x680 was chosen") -- directly as a
/// `RectifiedCard`, with NO call to `PerspectiveRectifier`.
///
/// This is a deliberate choice, not an oversight: `PerspectiveRectifier`
/// exists to turn an arbitrary quad detected in a camera frame into
/// canonical geometry. A Scryfall render already IS that geometry, so
/// routing it through the rectifier would mean inventing a synthetic
/// identity `CardQuad` and running an extra `warpPerspective` resample of a
/// stage this gate is not chartered to test -- `PerspectiveRectifier` has
/// its own marker-based tests, and B5a/B5b's real-capture tests already
/// exercise it against actual camera frames. Round-tripping through it here
/// would risk masking THIS gate's actual job (steps 4-6 plus
/// `QueryTransform`, i.e. "the query-side transform and hash are the
/// shipping ones") behind an unrelated resample whose own interpolation
/// choice (`INTER_LINEAR`, pinned) has nothing to do with what B2 measures.
/// stream-b-identification.md "B2", research target 5, reaches the same
/// conclusion for the INDEX side: "a dummy CardQuad ... serves as
/// sourceQuad; harmless" -- no warp needed, because the render already is
/// 488x680. The query side of B2 is the same situation.
///
/// The dummy `CardQuad` this produces is pure provenance metadata:
/// `QueryTransform`, `CardHasher` and `HashCardIdentifier` never read
/// `RectifiedCard.SourceQuad`, so its exact corner values are inert.
public static class CardImageLoader
{
    public static RectifiedCard ToRectifiedCard(Mat canonicalBgr)
    {
        ArgumentNullException.ThrowIfNull(canonicalBgr);
        if (canonicalBgr.Rows != RectifiedCard.CanonicalHeight || canonicalBgr.Cols != RectifiedCard.CanonicalWidth)
        {
            throw new ArgumentException(
                $"Expected a {RectifiedCard.CanonicalWidth}x{RectifiedCard.CanonicalHeight} BGR mat, got " +
                $"{canonicalBgr.Cols}x{canonicalBgr.Rows}.", nameof(canonicalBgr));
        }

        if (canonicalBgr.Channels() != 3)
        {
            throw new ArgumentException(
                $"Expected a 3-channel BGR mat, got {canonicalBgr.Channels()} channel(s). " +
                "Cv2.ImRead(..., ImreadModes.Color) always decodes 3 channels.", nameof(canonicalBgr));
        }

        var stride = RectifiedCard.CanonicalWidth * 3;
        var pixels = new byte[stride * RectifiedCard.CanonicalHeight];
        for (var row = 0; row < RectifiedCard.CanonicalHeight; row++)
        {
            Marshal.Copy(canonicalBgr.Ptr(row), pixels, row * stride, stride);
        }

        var quad = new CardQuad(
            TL: new PointF2(0, 0),
            TR: new PointF2(RectifiedCard.CanonicalWidth, 0),
            BR: new PointF2(RectifiedCard.CanonicalWidth, RectifiedCard.CanonicalHeight),
            BL: new PointF2(0, RectifiedCard.CanonicalHeight));

        return new RectifiedCard(pixels, stride, PixelLayout.Bgr24, quad);
    }
}
