using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Trigger;
using Xunit;

namespace LoreFetch.Tests.StreamA;

/// Unit tests for `AutoCaptureTrigger` — docs/TESTING.md calls it "the
/// highest-value unit target in the project" and lists the required cases;
/// this file also covers docs/design/app.md A0's nearest-centroid matching
/// requirement, since that is the one detail a naive port gets wrong, plus
/// two review findings: settle is measured against a fixed anchor snapshot
/// rather than the previous frame (otherwise slow drift under ε per frame
/// still fires the trigger over a full window), and movement is measured as
/// the max per-corner displacement of a matched quad rather than centroid
/// distance (otherwise an in-place rotation, which barely moves the
/// centroid, never resets the settle window).
///
/// `now` is always a fixed base plus an explicit offset — the trigger has no
/// clock of its own, so every test controls time completely.
public class AutoCaptureTriggerTests
{
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static AutoCaptureTrigger Create(int settleMilliseconds = 500, int movementTolerancePixels = 4) =>
        new(new ScanSettings
        {
            SettleMilliseconds = settleMilliseconds,
            MovementTolerancePixels = movementTolerancePixels,
        });

    /// A square quad centered at (centerX, centerY). The trigger only ever
    /// looks at corner positions (via the centroid), so size and aspect
    /// ratio are irrelevant to it — a fixed half-size keeps callers terse.
    private static CardQuad Quad(float centerX, float centerY, float halfSize = 25f) =>
        new(
            new PointF2(centerX - halfSize, centerY - halfSize),
            new PointF2(centerX + halfSize, centerY - halfSize),
            new PointF2(centerX + halfSize, centerY + halfSize),
            new PointF2(centerX - halfSize, centerY + halfSize));

    private static DateTimeOffset At(int milliseconds) => Base + TimeSpan.FromMilliseconds(milliseconds);

    /// A square quad of the given half-size, centered at (centerX, centerY)
    /// and rotated by `angleDegrees` about that same centre. Used to prove
    /// that rotation-in-place — which barely moves the centroid — is still
    /// caught by the per-corner displacement check.
    private static CardQuad RotatedQuad(float centerX, float centerY, float halfSize, double angleDegrees)
    {
        var angle = angleDegrees * Math.PI / 180.0;
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);

        PointF2 Rotate(float dx, float dy)
        {
            var rx = (dx * cos) - (dy * sin);
            var ry = (dx * sin) + (dy * cos);
            return new PointF2(centerX + (float)rx, centerY + (float)ry);
        }

        return new CardQuad(
            Rotate(-halfSize, -halfSize),
            Rotate(halfSize, -halfSize),
            Rotate(halfSize, halfSize),
            Rotate(-halfSize, halfSize));
    }

    // -- fires exactly once when stable for >= SettleMilliseconds --------

    [Fact]
    public void FiresOnceAfterCountIsStableForSettleWindow()
    {
        var trigger = Create();
        var quads = new[] { Quad(100, 100) };

        Assert.False(trigger.Evaluate(quads, expectedCount: 1, At(0)));
        Assert.False(trigger.Evaluate(quads, expectedCount: 1, At(250)));
        Assert.True(trigger.Evaluate(quads, expectedCount: 1, At(500)));
    }

    // -- the re-arm rule: a static tableau must not fire every window ----

    [Fact]
    public void DoesNotReFireWhileTheSceneStaysStatic()
    {
        var trigger = Create();
        var quads = new[] { Quad(100, 100) };

        Assert.False(trigger.Evaluate(quads, expectedCount: 1, At(0)));
        Assert.True(trigger.Evaluate(quads, expectedCount: 1, At(500)));

        // Same scene, held for many more settle windows: must never fire
        // again without a break. This is the exact bug a missing re-arm
        // rule produces — it would fire again as soon as it is asked.
        Assert.False(trigger.Evaluate(quads, expectedCount: 1, At(600)));
        Assert.False(trigger.Evaluate(quads, expectedCount: 1, At(1_000)));
        Assert.False(trigger.Evaluate(quads, expectedCount: 1, At(5_000)));
    }

    // -- re-arms only after the scene breaks (count mismatch) -------------

    [Fact]
    public void ReArmsAfterCountDropsToZeroThenRefiresOnFreshSettle()
    {
        var trigger = Create();
        var quads = new[] { Quad(100, 100) };

        Assert.False(trigger.Evaluate(quads, expectedCount: 1, At(0)));
        Assert.True(trigger.Evaluate(quads, expectedCount: 1, At(500)));

        // Scene breaks: the card is lifted away, count drops to zero.
        Assert.False(trigger.Evaluate(Array.Empty<CardQuad>(), expectedCount: 1, At(600)));

        // Put back down: a brand new settle window is required.
        Assert.False(trigger.Evaluate(quads, expectedCount: 1, At(700)));
        Assert.False(trigger.Evaluate(quads, expectedCount: 1, At(1_100)));
        Assert.True(trigger.Evaluate(quads, expectedCount: 1, At(1_200)));
    }

    /// The override that actually governs this trigger (docs/design/app.md
    /// A0, and the task brief for this package): "the scene breaks" means
    /// count != expected — movement alone resets the settle TIMER but must
    /// NOT re-arm an already-fired trigger, because there was no count
    /// mismatch. Without this distinction a card nudged half an inch while
    /// still alone on the mat would silently re-fire a second cohort.
    [Fact]
    public void MovementAloneWithoutACountChangeDoesNotReArm()
    {
        var trigger = Create();
        var quads = new[] { Quad(100, 100) };

        Assert.False(trigger.Evaluate(quads, expectedCount: 1, At(0)));
        Assert.True(trigger.Evaluate(quads, expectedCount: 1, At(500)));

        // Card slides far away, but the count never changes (still 1).
        var moved = new[] { Quad(400, 400) };
        Assert.False(trigger.Evaluate(moved, expectedCount: 1, At(600)));

        // Even after a full new settle window at the new position, it must
        // stay unarmed — only a count mismatch re-arms it.
        Assert.False(trigger.Evaluate(moved, expectedCount: 1, At(1_100)));
        Assert.False(trigger.Evaluate(moved, expectedCount: 1, At(2_000)));
    }

    // -- never fires on a count mismatch -----------------------------------

    [Fact]
    public void NeverFiresWhenCountNeverMatchesExpected()
    {
        var trigger = Create();
        var quads = new[] { Quad(100, 100), Quad(200, 100) }; // 2, expecting 3

        for (var ms = 0; ms <= 5_000; ms += 250)
        {
            Assert.False(trigger.Evaluate(quads, expectedCount: 3, At(ms)));
        }
    }

    // -- NotifyCaptured() (manual path) suppresses an immediate re-fire ---

    [Fact]
    public void NotifyCapturedSuppressesAnImmediateAutoFireOnTheSameScene()
    {
        var trigger = Create();
        var quads = new[] { Quad(100, 100) };

        // Settle is under way but has not reached 500ms yet when the user
        // presses Space — the manual path always calls NotifyCaptured().
        Assert.False(trigger.Evaluate(quads, expectedCount: 1, At(0)));
        trigger.NotifyCaptured();

        // The scene never broke, so without suppression this would fire.
        Assert.False(trigger.Evaluate(quads, expectedCount: 1, At(500)));
        Assert.False(trigger.Evaluate(quads, expectedCount: 1, At(1_000)));

        // Only a real scene break re-arms it.
        Assert.False(trigger.Evaluate(Array.Empty<CardQuad>(), expectedCount: 1, At(1_100)));
        Assert.False(trigger.Evaluate(quads, expectedCount: 1, At(1_200)));
        Assert.True(trigger.Evaluate(quads, expectedCount: 1, At(1_700)));
    }

    // -- nearest-centroid matching, not list index -------------------------

    /// The scenario the task brief calls out by name: two quads of
    /// near-identical area can swap order in `ICardDetector`'s
    /// descending-by-area list from one frame to the next, purely from
    /// measurement noise. Matching by list index reads that swap as both
    /// cards jumping across the table; matching by nearest centroid reads
    /// it correctly as "nothing moved". This test fires only if the settle
    /// window survives the swap intact.
    [Fact]
    public void SwappedListOrderBetweenNearEqualAreaQuadsDoesNotResetSettle()
    {
        var trigger = Create();
        var cardA = Quad(100, 100);
        var cardB = Quad(300, 100);

        Assert.False(trigger.Evaluate(new[] { cardA, cardB }, expectedCount: 2, At(0)));

        // Same two cards, same positions, but the list order has swapped —
        // exactly what near-equal area plus measurement noise produces.
        Assert.False(trigger.Evaluate(new[] { cardB, cardA }, expectedCount: 2, At(100)));

        // Swap back for good measure.
        Assert.False(trigger.Evaluate(new[] { cardA, cardB }, expectedCount: 2, At(250)));

        // If the swaps had reset the settle timer, only 250ms would have
        // elapsed since the last reset at t=250 and this would be false.
        Assert.True(trigger.Evaluate(new[] { cardA, cardB }, expectedCount: 2, At(500)));
    }

    // -- boundary: fires at exactly the CONFIGURED settle, not a hardcoded one --

    [Fact]
    public void FiresAtExactlyTheConfiguredSettleMillisecondsNotBefore()
    {
        // A non-default value so a hard-coded threshold elsewhere in the
        // implementation cannot coincidentally satisfy this test.
        var trigger = Create(settleMilliseconds: 750);
        var quads = new[] { Quad(100, 100) };

        Assert.False(trigger.Evaluate(quads, expectedCount: 1, At(0)));
        Assert.False(trigger.Evaluate(quads, expectedCount: 1, At(400)));
        Assert.False(trigger.Evaluate(quads, expectedCount: 1, At(749)));
        Assert.True(trigger.Evaluate(quads, expectedCount: 1, At(750)));
    }

    // -- movement tolerance: within ε does not reset, beyond ε does ------

    [Fact]
    public void MovementWithinToleranceDoesNotResetTheSettleWindow()
    {
        var trigger = Create(movementTolerancePixels: 4);

        Assert.False(trigger.Evaluate(new[] { Quad(100, 100) }, expectedCount: 1, At(0)));
        // 3px jitter, under the 4px tolerance.
        Assert.False(trigger.Evaluate(new[] { Quad(103, 100) }, expectedCount: 1, At(250)));
        Assert.True(trigger.Evaluate(new[] { Quad(100, 100) }, expectedCount: 1, At(500)));
    }

    [Fact]
    public void MovementBeyondToleranceResetsTheSettleWindow()
    {
        var trigger = Create(movementTolerancePixels: 4);

        Assert.False(trigger.Evaluate(new[] { Quad(100, 100) }, expectedCount: 1, At(0)));
        // 50px shift, well beyond the 4px tolerance: settle must restart.
        Assert.False(trigger.Evaluate(new[] { Quad(150, 100) }, expectedCount: 1, At(250)));

        // Only 250ms have elapsed since the reset at t=250, so this must
        // still be false — if it were true, the reset above did nothing.
        Assert.False(trigger.Evaluate(new[] { Quad(150, 100) }, expectedCount: 1, At(500)));
        Assert.True(trigger.Evaluate(new[] { Quad(150, 100) }, expectedCount: 1, At(750)));
    }

    // -- Reset() ------------------------------------------------------------

    [Fact]
    public void ResetClearsFiredStateSoTheNextFreshSettleFiresAgain()
    {
        var trigger = Create();
        var quads = new[] { Quad(100, 100) };

        Assert.False(trigger.Evaluate(quads, expectedCount: 1, At(0)));
        Assert.True(trigger.Evaluate(quads, expectedCount: 1, At(500)));

        trigger.Reset();

        // Reset re-arms and clears the settle window outright — a fresh
        // 500ms run fires again with no intervening count mismatch needed.
        Assert.False(trigger.Evaluate(quads, expectedCount: 1, At(600)));
        Assert.True(trigger.Evaluate(quads, expectedCount: 1, At(1_100)));
    }

    // -- anchor vs. previous-frame comparison (orchestrator review finding) --

    /// A card sliding steadily at 3px per 33ms frame never exceeds ε (4px)
    /// from one frame to the next, but travels ~54px over an 600ms window —
    /// comparing only to the previous frame would let every single step
    /// pass and the trigger would fire on a moving hand. Comparing against
    /// a fixed anchor catches the accumulated drift within a couple of
    /// frames and keeps restarting the window, so it must never fire here.
    [Fact]
    public void SlowDriftUnderEpsilonPerFrameNeverFiresOverASettleWindow()
    {
        var trigger = Create(settleMilliseconds: 500, movementTolerancePixels: 4);
        const double pixelsPerStep = 3.0;
        const int stepMilliseconds = 33;

        for (var step = 0; step * stepMilliseconds <= 600; step++)
        {
            var x = 100f + (float)(pixelsPerStep * step);
            var fired = trigger.Evaluate(new[] { Quad(x, 100) }, expectedCount: 1, At(step * stepMilliseconds));
            Assert.False(fired);
        }
    }

    /// A card rotating a few degrees per frame about its own centre barely
    /// moves its centroid at all, so a centroid-only movement test would
    /// never reset the settle window and the trigger would fire on a
    /// spinning card. Measuring the max per-corner displacement catches it:
    /// every frame moves every corner well past ε, so this must never fire.
    [Fact]
    public void RotationAboutItsOwnCentreResetsSettleEvenThoughTheCentroidBarelyMoves()
    {
        var trigger = Create(settleMilliseconds: 500, movementTolerancePixels: 4);
        const double degreesPerStep = 10.0;
        const int stepMilliseconds = 33;

        for (var step = 0; step * stepMilliseconds <= 600; step++)
        {
            var quad = RotatedQuad(100, 100, halfSize: 25f, angleDegrees: degreesPerStep * step);
            var fired = trigger.Evaluate(new[] { quad }, expectedCount: 1, At(step * stepMilliseconds));
            Assert.False(fired);
        }
    }
}
