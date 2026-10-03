using LoreFetch.App;
using LoreFetch.Core.Abstractions;
using Xunit;

namespace LoreFetch.Tests.App;

/// `QuadOverlayDeadband`'s rule on its own, one `Apply` per rendered frame.
/// `QuadOverlayGeometryTests` checks it is wired into the drawn overlay.
public class QuadOverlayDeadbandTests
{
    private static readonly CardQuad Card = new(
        new PointF2(100, 100), new PointF2(200, 100), new PointF2(200, 240), new PointF2(100, 240));

    private static readonly CardQuad OtherCard = Shift(Card, dx: 300, dy: 0);

    [Fact]
    public void CornerJitterWithinTolerance_KeepsTheDrawnQuad()
    {
        var deadband = new QuadOverlayDeadband();
        deadband.Apply([Card]);

        var jittered = new CardQuad(
            new PointF2(101, 99), new PointF2(198, 101), new PointF2(202, 241), new PointF2(100, 237));

        Assert.Equal(new[] { Card }, deadband.Apply([jittered]));
    }

    [Fact]
    public void FlipFlopBetweenTwoOutlines_NeverMovesTheBorder()
    {
        // The recorded case: a square-on card's outline alternating with one
        // whose top-left corner sits ~8 px away, beyond the tolerance, with
        // the occasional run of two.
        var flipped = new CardQuad(new PointF2(94, 95), Card.TR, Card.BR, new PointF2(97, 240));
        var deadband = new QuadOverlayDeadband();
        deadband.Apply([Card]);

        CardQuad[][] pattern = [[flipped], [Card], [flipped], [flipped], [Card], [flipped], [Card]];
        for (var round = 0; round < 5; round++)
        {
            foreach (var frame in pattern)
            {
                Assert.Equal(new[] { Card }, deadband.Apply(frame));
            }
        }
    }

    [Fact]
    public void RealMove_RedrawsOnceItHasHeldForThreeRenders()
    {
        var deadband = new QuadOverlayDeadband();
        deadband.Apply([Card]);

        var moved = Shift(Card, dx: 15, dy: 0);

        Assert.Equal(new[] { Card }, deadband.Apply([moved]));
        Assert.Equal(new[] { Card }, deadband.Apply([moved]));
        Assert.Equal(new[] { moved }, deadband.Apply([moved]));
        Assert.Equal(new[] { moved }, deadband.Apply([moved]));
    }

    [Fact]
    public void SlowDrift_IsMeasuredFromTheDrawnQuad_SoItCatchesUp()
    {
        // 1.5 px a render: no single step exceeds the tolerance, but from the
        // third render on the card is more than 4 px from where it is drawn.
        var deadband = new QuadOverlayDeadband();
        deadband.Apply([Card]);

        Assert.Equal(new[] { Card }, deadband.Apply([Shift(Card, 1.5f, 0)]));
        Assert.Equal(new[] { Card }, deadband.Apply([Shift(Card, 3.0f, 0)]));
        Assert.Equal(new[] { Card }, deadband.Apply([Shift(Card, 4.5f, 0)]));
        Assert.Equal(new[] { Card }, deadband.Apply([Shift(Card, 6.0f, 0)]));
        Assert.Equal(new[] { Shift(Card, 7.5f, 0) }, deadband.Apply([Shift(Card, 7.5f, 0)]));
    }

    [Fact]
    public void EachCard_IsHeldIndependently()
    {
        var deadband = new QuadOverlayDeadband();
        deadband.Apply([Card, OtherCard]);

        var otherMoved = Shift(OtherCard, dx: 0, dy: 25);
        deadband.Apply([otherMoved, Shift(Card, 1, 1)]);
        deadband.Apply([otherMoved, Shift(Card, -1, 1)]);
        var result = deadband.Apply([otherMoved, Shift(Card, 1, -1)]);

        Assert.Equal(new[] { otherMoved, Card }, result);
    }

    [Fact]
    public void NewCard_IsDrawnImmediately()
    {
        var deadband = new QuadOverlayDeadband();
        deadband.Apply([Card]);

        Assert.Equal(new[] { Card, OtherCard }, deadband.Apply([Card, OtherCard]));
    }

    private static CardQuad Shift(CardQuad q, float dx, float dy) => new(
        new PointF2(q.TL.X + dx, q.TL.Y + dy),
        new PointF2(q.TR.X + dx, q.TR.Y + dy),
        new PointF2(q.BR.X + dx, q.BR.Y + dy),
        new PointF2(q.BL.X + dx, q.BL.Y + dy));
}
