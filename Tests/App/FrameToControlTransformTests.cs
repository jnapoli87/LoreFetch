using Avalonia;
using LoreFetch.App;
using LoreFetch.Core.Abstractions;
using Xunit;

namespace LoreFetch.Tests.App;

/// Unit tests for `FrameToControlTransform` (A3) — the pure frame->control
/// mapping the quad overlay uses to place a `CardQuad`'s corners on top of
/// `PreviewImage`, whose `Stretch="Uniform"` letterboxes rather than
/// distorts. Every case below uses dimensions chosen so the arithmetic lands
/// on exact binary fractions (e.g. 900/1920 = 0.46875 = 15/32), so the
/// assertions compare doubles exactly rather than within a tolerance.
public class FrameToControlTransformTests
{
    [Fact]
    public void Compute_WiderControlThanFrame_LetterboxesLeftAndRightAndMapsCornersExactly()
    {
        // frame 640x480 (4:3), control 800x480: height binds (ratio 1.0),
        // width has 160px of slack split into two 80px bars.
        var transform = FrameToControlTransform.Compute(frameWidth: 640, frameHeight: 480, controlWidth: 800, controlHeight: 480);

        Assert.Equal(1.0, transform.Scale);
        Assert.Equal(80.0, transform.OffsetX);
        Assert.Equal(0.0, transform.OffsetY);

        Assert.Equal(new Point(80, 0), transform.Apply(new PointF2(0, 0)));
        Assert.Equal(new Point(720, 480), transform.Apply(new PointF2(640, 480)));
    }

    [Fact]
    public void Compute_RotatedPortraitFrameInLandscapeControl_MapsCornersExactly()
    {
        // A 1080x1920 (portrait, post-rotation) frame in a 1600x900
        // landscape control — the case docs/design/app.md A3 calls out by
        // name. Height binds: min(1600/1080, 900/1920) = min(1.4814..., 0.46875).
        var transform = FrameToControlTransform.Compute(frameWidth: 1080, frameHeight: 1920, controlWidth: 1600, controlHeight: 900);

        Assert.Equal(0.46875, transform.Scale);
        Assert.Equal(546.875, transform.OffsetX);
        Assert.Equal(0.0, transform.OffsetY);

        Assert.Equal(new Point(546.875, 0), transform.Apply(new PointF2(0, 0)));
        Assert.Equal(new Point(1053.125, 900), transform.Apply(new PointF2(1080, 1920)));
    }

    [Fact]
    public void Compute_LandscapeFrameInPortraitControl_MapsCornersExactly()
    {
        // The mirror image of the case above: a 1920x1080 landscape frame in
        // a 900x1600 portrait control. Width binds this time.
        var transform = FrameToControlTransform.Compute(frameWidth: 1920, frameHeight: 1080, controlWidth: 900, controlHeight: 1600);

        Assert.Equal(0.46875, transform.Scale);
        Assert.Equal(0.0, transform.OffsetX);
        Assert.Equal(546.875, transform.OffsetY);

        Assert.Equal(new Point(0, 546.875), transform.Apply(new PointF2(0, 0)));
        Assert.Equal(new Point(900, 1053.125), transform.Apply(new PointF2(1920, 1080)));
    }
}
