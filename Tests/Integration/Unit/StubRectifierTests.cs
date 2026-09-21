using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Fakes;
using Xunit;

namespace LoreFetch.Tests.Integration.Unit;

public class StubRectifierTests
{
    [Theory]
    [InlineData(PixelLayout.Bgr24, 3)]
    [InlineData(PixelLayout.Bgra32, 4)]
    public void Rectify_OutputIsCanonicalSize_WithCorrectStrideAndLayout(PixelLayout layout, int bytesPerPixel)
    {
        using var frame = MakeFrame(400, 600, layout);
        var quad = new CardQuad(
            TL: new PointF2(50, 60),
            TR: new PointF2(350, 60),
            BR: new PointF2(350, 540),
            BL: new PointF2(50, 540));

        var card = new StubRectifier().Rectify(frame, quad);

        Assert.Equal(RectifiedCard.CanonicalWidth * bytesPerPixel, card.Stride);
        Assert.Equal(card.Stride * RectifiedCard.CanonicalHeight, card.Pixels.Length);
        Assert.Equal(layout, card.Layout);
        Assert.Equal(quad, card.SourceQuad);
    }

    [Fact]
    public void Rectify_QuadPartlyOffFrame_ClampsAndDoesNotThrowOrReadOutOfBounds()
    {
        using var frame = MakeFrame(200, 300, PixelLayout.Bgr24);

        // Runs past the right and bottom edges of a 200x300 frame.
        var quad = new CardQuad(
            TL: new PointF2(150, 250),
            TR: new PointF2(400, 250),
            BR: new PointF2(400, 500),
            BL: new PointF2(150, 500));

        var card = new StubRectifier().Rectify(frame, quad);

        Assert.Equal(RectifiedCard.CanonicalWidth * 3, card.Stride);
        Assert.Equal(card.Stride * RectifiedCard.CanonicalHeight, card.Pixels.Length);
    }

    [Fact]
    public void Rectify_QuadEntirelyOffFrame_ClampsToASinglePixel_AndDoesNotThrow()
    {
        using var frame = MakeFrame(200, 300, PixelLayout.Bgr24);

        // Entirely beyond the bottom-right corner of a 200x300 frame.
        var quad = new CardQuad(
            TL: new PointF2(500, 600),
            TR: new PointF2(700, 600),
            BR: new PointF2(700, 800),
            BL: new PointF2(500, 800));

        var card = new StubRectifier().Rectify(frame, quad);

        Assert.Equal(RectifiedCard.CanonicalWidth * 3, card.Stride);
        Assert.Equal(card.Stride * RectifiedCard.CanonicalHeight, card.Pixels.Length);
    }

    [Fact]
    public void Rectify_QuadWithNegativeCoordinates_ClampsAndDoesNotThrow()
    {
        using var frame = MakeFrame(200, 300, PixelLayout.Bgr24);

        var quad = new CardQuad(
            TL: new PointF2(-80, -80),
            TR: new PointF2(120, -80),
            BR: new PointF2(120, 220),
            BL: new PointF2(-80, 220));

        var card = new StubRectifier().Rectify(frame, quad);

        Assert.Equal(RectifiedCard.CanonicalWidth * 3, card.Stride);
        Assert.Equal(card.Stride * RectifiedCard.CanonicalHeight, card.Pixels.Length);
    }

    [Fact]
    public void Rectify_RespectsStrideWiderThanWidthTimesBpp()
    {
        const int width = 100;
        const int height = 150;
        const int bpp = 3;
        const int padding = 16;
        var stride = (width * bpp) + padding;

        var buffer = new byte[stride * height];
        // Mark the padding bytes with a value the rectified output must
        // never pick up, proving the crop honours Stride rather than
        // assuming Stride == Width * bpp.
        for (var row = 0; row < height; row++)
        {
            for (var i = width * bpp; i < stride; i++)
            {
                buffer[(row * stride) + i] = 0xFF;
            }
        }

        using var frame = new CameraFrame(
            buffer, width, height, stride, PixelLayout.Bgr24, DateTimeOffset.UtcNow, pool: null);
        var quad = new CardQuad(
            TL: new PointF2(0, 0),
            TR: new PointF2(width, 0),
            BR: new PointF2(width, height),
            BL: new PointF2(0, height));

        var card = new StubRectifier().Rectify(frame, quad);

        Assert.DoesNotContain((byte)0xFF, card.Pixels.ToArray());
    }

    private static CameraFrame MakeFrame(int width, int height, PixelLayout layout)
    {
        var bytesPerPixel = layout == PixelLayout.Bgr24 ? 3 : 4;
        var stride = width * bytesPerPixel;
        var buffer = new byte[stride * height];

        var random = new Random(42);
        random.NextBytes(buffer);

        return new CameraFrame(buffer, width, height, stride, layout, DateTimeOffset.UtcNow, pool: null);
    }
}
