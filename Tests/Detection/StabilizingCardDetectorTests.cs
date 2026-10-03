using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Detection;
using Xunit;

namespace LoreFetch.Tests.Detection;

/// `StabilizingCardDetector` over a scripted inner detector, one script entry
/// per frame, so every test spells out exactly which frames the card was
/// found on. Default options throughout: confirm on 3 hits in 5 frames,
/// hold through 8 misses.
public class StabilizingCardDetectorTests
{
    private static readonly CardQuad Card = Quad(centreX: 200, centreY: 200);

    // A 3x3 neighbour: one short side (100 px) plus a gap to the right.
    private static readonly CardQuad Neighbour = Quad(centreX: 310, centreY: 200);

    [Fact]
    public void CardSeenOnce_IsNeverReported()
    {
        var outputs = Run([[Card], [], [], [], [], []]);

        Assert.All(outputs, frame => Assert.Empty(frame));
    }

    [Fact]
    public void FlickeringCard_IsReportedOnItsThirdHit_AndThenStaysOn()
    {
        // On, off, on, off, on: the third hit lands on frame 5.
        var outputs = Run([[Card], [], [Card], [], [Card], [], [Card], []]);

        Assert.All(outputs[..4], frame => Assert.Empty(frame));
        Assert.All(outputs[4..], frame => Assert.Equal(new[] { Card }, frame));
    }

    [Fact]
    public void SteadyCard_IsReportedFromItsThirdFrame()
    {
        var outputs = Run([[Card], [Card], [Card], [Card]]);

        Assert.Empty(outputs[0]);
        Assert.Empty(outputs[1]);
        Assert.Equal(new[] { Card }, outputs[2]);
        Assert.Equal(new[] { Card }, outputs[3]);
    }

    [Fact]
    public void ConfirmedCard_IsHeldThroughEightMisses_AtItsLastQuad_ThenDropped()
    {
        var moved = Shift(Card, dx: 3, dy: 2);
        var script = new List<CardQuad[]> { new[] { Card }, new[] { Card }, new[] { moved } };
        script.AddRange(Enumerable.Repeat(Array.Empty<CardQuad>(), 9));

        var outputs = Run(script);

        Assert.All(outputs[3..11], frame => Assert.Equal(new[] { moved }, frame));
        Assert.Empty(outputs[11]);
    }

    [Fact]
    public void SeenCard_PassesTheNewestQuadThrough_NotTheFirstOne()
    {
        // Capture rectifies these quads, so a card that slides must be
        // reported where it is now.
        var slid = Shift(Card, dx: 20, dy: -10);

        var outputs = Run([[Card], [Card], [Card], [slid]]);

        Assert.Equal(new[] { slid }, outputs[3]);
    }

    [Fact]
    public void FlickeringCard_DoesNotDisturbASteadyNeighbour()
    {
        var outputs = Run(
        [
            [Card, Neighbour],
            [Card],
            [Card, Neighbour],
            [Card],
            [Card, Neighbour],
            [Card],
        ]);

        Assert.Equal(new[] { Card }, outputs[2]);
        Assert.All(outputs[4..], frame => Assert.Equal(2, frame.Count));
    }

    [Fact]
    public void Output_IsOrderedByDescendingArea_AndCappedAtMaxCards()
    {
        var small = Quad(centreX: 500, centreY: 200, shortSide: 80);
        var large = Quad(centreX: 200, centreY: 200, shortSide: 120);
        var medium = Quad(centreX: 350, centreY: 200, shortSide: 100);
        CardQuad[] frame = [small, large, medium];

        var outputs = Run([frame, frame, frame], maxCards: 2);

        Assert.Equal(new[] { large, medium }, outputs[2]);
    }

    [Fact]
    public void Constructor_RejectsMoreConfirmHitsThanTheWindowHolds() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new StabilizingCardDetector(
            new ScriptedDetector([]), new CardStabilizerOptions { ConfirmHits = 6, ConfirmWindow = 5 }));

    private static List<IReadOnlyList<CardQuad>> Run(IReadOnlyList<CardQuad[]> script, int maxCards = 9)
    {
        var detector = new StabilizingCardDetector(new ScriptedDetector(script));
        using var frame = new CameraFrame(new byte[3], 1, 1, 3, PixelLayout.Bgr24, DateTimeOffset.UnixEpoch, pool: null);

        return script.Select(_ => detector.Detect(frame, maxCards)).ToList();
    }

    /// An upright card of the real 63:88 aspect, centred on the given point.
    private static CardQuad Quad(float centreX, float centreY, float shortSide = 100)
    {
        var halfW = shortSide / 2f;
        var halfH = shortSide * 88f / 63f / 2f;
        return new CardQuad(
            new PointF2(centreX - halfW, centreY - halfH),
            new PointF2(centreX + halfW, centreY - halfH),
            new PointF2(centreX + halfW, centreY + halfH),
            new PointF2(centreX - halfW, centreY + halfH));
    }

    private static CardQuad Shift(CardQuad q, float dx, float dy) => new(
        new PointF2(q.TL.X + dx, q.TL.Y + dy),
        new PointF2(q.TR.X + dx, q.TR.Y + dy),
        new PointF2(q.BR.X + dx, q.BR.Y + dy),
        new PointF2(q.BL.X + dx, q.BL.Y + dy));

    /// Returns the next scripted frame's quads on each call.
    private sealed class ScriptedDetector(IReadOnlyList<CardQuad[]> script) : ICardDetector
    {
        private int _call;

        public IReadOnlyList<CardQuad> Detect(CameraFrame frame, int maxCards) => script[_call++];
    }
}
