using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Fakes;
using Xunit;

namespace LoreFetch.Tests.Integration.Unit;

public class StubCardDetectorTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(9)]
    public void Detect_ReturnsRequestedCount_AllInsideFrameWithCardAspectRatio_DescendingArea(int count)
    {
        var detector = new StubCardDetector(count);
        using var frame = MakeFrame(1360, 1020);

        var quads = detector.Detect(frame, maxCards: 9);

        Assert.Equal(count, quads.Count);
        foreach (var quad in quads)
        {
            AssertInsideFrame(quad, frame.Width, frame.Height);
            Assert.InRange(quad.AspectRatio, 1.397f * 0.95f, 1.397f * 1.05f);
        }

        AssertNonIncreasingArea(quads);
    }

    [Theory]
    [InlineData(9, 3)]
    [InlineData(3, 1)]
    [InlineData(1, 0)]
    public void Detect_NeverExceedsMaxCards(int cardCount, int maxCards)
    {
        var detector = new StubCardDetector(cardCount);
        using var frame = MakeFrame(800, 1200);

        var quads = detector.Detect(frame, maxCards);

        Assert.True(quads.Count <= maxCards);
        Assert.Equal(Math.Min(cardCount, maxCards), quads.Count);
    }

    [Fact]
    public void Detect_ZeroCardCount_ReturnsEmpty()
    {
        var detector = new StubCardDetector(0);
        using var frame = MakeFrame(800, 1200);

        var quads = detector.Detect(frame, maxCards: 9);

        Assert.Empty(quads);
    }

    [Fact]
    public void Detect_WorksForBothPortraitAndLandscapeFrames()
    {
        var detector = new StubCardDetector(9);

        using (var landscape = MakeFrame(1920, 1080))
        {
            var quads = detector.Detect(landscape, 9);
            Assert.Equal(9, quads.Count);
            foreach (var quad in quads)
            {
                AssertInsideFrame(quad, landscape.Width, landscape.Height);
            }
        }

        using (var portrait = MakeFrame(1080, 1920))
        {
            var quads = detector.Detect(portrait, 9);
            Assert.Equal(9, quads.Count);
            foreach (var quad in quads)
            {
                AssertInsideFrame(quad, portrait.Width, portrait.Height);
            }
        }
    }

    [Fact]
    public void CardCount_IsSettableAfterConstruction()
    {
        var detector = new StubCardDetector(1);
        using var frame = MakeFrame(800, 1200);

        detector.CardCount = 3;
        var quads = detector.Detect(frame, maxCards: 9);

        Assert.Equal(3, quads.Count);
    }

    private static CameraFrame MakeFrame(int width, int height)
    {
        var stride = width * 3;
        var buffer = new byte[stride * height];
        return new CameraFrame(buffer, width, height, stride, PixelLayout.Bgr24, DateTimeOffset.UtcNow, pool: null);
    }

    private static void AssertInsideFrame(CardQuad quad, int width, int height)
    {
        foreach (var point in new[] { quad.TL, quad.TR, quad.BR, quad.BL })
        {
            Assert.InRange(point.X, 0f, (float)width);
            Assert.InRange(point.Y, 0f, (float)height);
        }
    }

    private static void AssertNonIncreasingArea(IReadOnlyList<CardQuad> quads)
    {
        for (var i = 1; i < quads.Count; i++)
        {
            Assert.True(
                quads[i - 1].AreaPx >= quads[i].AreaPx - 0.01f,
                $"quad {i - 1} (area {quads[i - 1].AreaPx}) should be >= quad {i} (area {quads[i].AreaPx})");
        }
    }
}
