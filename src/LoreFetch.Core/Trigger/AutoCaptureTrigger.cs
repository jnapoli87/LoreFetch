using LoreFetch.Core.Abstractions;

namespace LoreFetch.Core.Trigger;

/// The count-gated settle trigger (CLAUDE.md "Interaction — capture, then
/// accept the cohort"): fires once the exact expected count has been held
/// stable for `ScanSettings.SettleMilliseconds`, then refuses to fire again
/// until "the scene breaks" — any snapshot whose quad count differs from
/// `expectedCount`, zero included (docs/design/app.md A0). Without that
/// re-arm rule a static tableau would re-fire every settle window forever.
///
/// "Stable" is measured against a fixed ANCHOR snapshot — the quads at the
/// moment the current settle window started — never against the previous
/// frame. Comparing frame-to-frame lets a slow drift (a few px per frame,
/// each step under ε) accumulate to tens of px over a 500ms window without
/// any single step ever tripping the tolerance, which fires the trigger on
/// a moving hand. Comparing every later snapshot to the same anchor makes
/// TOTAL displacement over the window the thing ε actually bounds; once a
/// snapshot exceeds ε from the anchor, that snapshot becomes the new anchor
/// and the window restarts.
///
/// Pure logic, no clock of its own: `now` arrives with every snapshot, which
/// is what makes this fully unit-testable without a fake clock. The scan
/// pipeline (`Core/Scanning`) is the only caller.
public sealed class AutoCaptureTrigger : IAutoCaptureTrigger
{
    private readonly int _settleMilliseconds;
    private readonly double _movementToleranceSquaredPixels;

    /// True until the trigger has fired (auto or via `NotifyCaptured`) and
    /// not yet seen the scene break. Starts true: a trigger that has never
    /// fired is, by definition, armed.
    private bool _armed = true;

    /// When the current run of "count == expectedCount, no excess movement"
    /// began. Null while the scene is not currently at the expected count,
    /// or right after a reset caused by a count change or movement.
    private DateTimeOffset? _settleStart;

    /// The quads at the moment the CURRENT settle window (re)started — fixed
    /// for the whole window, not updated every frame. Every later matching
    /// snapshot is compared against this anchor, never against the previous
    /// frame: comparing frame-to-frame lets a slow drift (a few px per
    /// frame, each step under ε) accumulate to tens of px over a 500ms
    /// window without any single step ever tripping the tolerance — which
    /// is exactly "the trigger fires on a moving hand". Comparing against a
    /// fixed anchor makes total displacement over the window the thing that
    /// gets measured, which is what ε is actually meant to bound. Null
    /// while the scene is not currently at the expected count, or right
    /// after a reset caused by a count change or excess movement (at which
    /// point the current snapshot becomes the new anchor).
    private IReadOnlyList<CardQuad>? _anchorQuads;

    /// Reads `SettleMilliseconds` and `MovementTolerancePixels` from
    /// settings once, at construction — this class does not watch for
    /// settings mutated later, matching every other tunable in the pipeline.
    public AutoCaptureTrigger(ScanSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _settleMilliseconds = settings.SettleMilliseconds;

        var toleranceSquared = (double)settings.MovementTolerancePixels * settings.MovementTolerancePixels;
        _movementToleranceSquaredPixels = toleranceSquared;
    }

    public bool Evaluate(IReadOnlyList<CardQuad> quads, int expectedCount, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(quads);

        // "The scene breaks" — defined in docs/design/app.md A0 as any
        // count mismatch, zero included. Breaking always re-arms and always
        // restarts the settle window; there is nothing "stable" to measure
        // across a count change.
        if (quads.Count != expectedCount)
        {
            _armed = true;
            _settleStart = null;
            _anchorQuads = null;
            return false;
        }

        if (_settleStart is null || _anchorQuads is null || HasMovedBeyondTolerance(_anchorQuads, quads))
        {
            // First matching snapshot after a break, or the cards have
            // drifted beyond ε from the anchor: (re)start the settle window
            // with THIS snapshot as the new anchor. Movement resets the
            // TIMER but is deliberately NOT a scene break, so it never
            // re-arms an already-fired trigger — only a count mismatch does
            // that.
            _settleStart = now;
            _anchorQuads = quads;
            return false;
        }

        // Still within ε of the anchor: do NOT advance the anchor to this
        // snapshot. Advancing it here is exactly the frame-to-frame
        // comparison that lets slow drift slip under ε on every single step.

        if (!_armed)
        {
            return false;
        }

        if (now - _settleStart.Value >= TimeSpan.FromMilliseconds(_settleMilliseconds))
        {
            _armed = false;
            return true;
        }

        return false;
    }

    public void NotifyCaptured()
    {
        // Mirrors what a firing Evaluate() already does to _armed, so a
        // manual capture (Space) suppresses an immediate auto-fire on the
        // same scene exactly as an auto-fire suppresses itself. The settle
        // window is left alone: it cannot cause a fire while unarmed, and
        // the next count mismatch clears it anyway.
        _armed = false;
    }

    public void Reset()
    {
        _armed = true;
        _settleStart = null;
        _anchorQuads = null;
    }

    /// Matches `current` quads to the `anchor` quads by nearest centroid —
    /// NOT by list index. `ICardDetector` orders by descending area, and two
    /// cards of near-identical area can swap places between frames; matching
    /// by index would read that swap as movement and reset the settle timer
    /// forever (docs/design/app.md A0). Both lists are already known to be
    /// the same length: both equal `expectedCount` at the call site.
    ///
    /// Greedy nearest-available matching rather than a globally optimal
    /// assignment: for the small counts here (1, 3 or 9) a simple ordered
    /// swap — the only case that actually occurs frame-to-frame — is
    /// resolved correctly by greedy matching, and an optimal assignment
    /// solver buys nothing a webcam scene will ever exercise.
    ///
    /// Once a quad is matched, the actual movement test is the MAX of the
    /// four corner-to-corner displacements between the matched anchor quad
    /// and the current one — not the centroid distance used for matching.
    /// A card rotated in place about its own centre moves every corner while
    /// its centroid barely shifts, and docs/design/app.md A0 is explicit
    /// that ε is measured by "compar[ing] corner positions". `CardQuad`
    /// corners are always ordered TL/TR/BR/BL (Detection.cs), so once two
    /// quads are matched their corners correspond index-for-index without
    /// any further matching step.
    private bool HasMovedBeyondTolerance(IReadOnlyList<CardQuad> anchor, IReadOnlyList<CardQuad> current)
    {
        var anchorCentroids = new PointF2[anchor.Count];
        for (var i = 0; i < anchor.Count; i++)
        {
            anchorCentroids[i] = Centroid(anchor[i]);
        }

        var used = new bool[anchor.Count];

        foreach (var quad in current)
        {
            var centroid = Centroid(quad);

            var bestIndex = -1;
            var bestCentroidDistanceSquared = double.MaxValue;

            for (var i = 0; i < anchorCentroids.Length; i++)
            {
                if (used[i])
                {
                    continue;
                }

                var dx = (double)centroid.X - anchorCentroids[i].X;
                var dy = (double)centroid.Y - anchorCentroids[i].Y;
                var distanceSquared = (dx * dx) + (dy * dy);

                if (distanceSquared < bestCentroidDistanceSquared)
                {
                    bestCentroidDistanceSquared = distanceSquared;
                    bestIndex = i;
                }
            }

            // anchor.Count == current.Count is guaranteed by the caller
            // (both already checked equal to expectedCount), so a free
            // anchor centroid always exists here.
            used[bestIndex] = true;

            if (MaxCornerDisplacementSquared(anchor[bestIndex], quad) > _movementToleranceSquaredPixels)
            {
                return true;
            }
        }

        return false;
    }

    private static double MaxCornerDisplacementSquared(CardQuad anchor, CardQuad current)
    {
        var tl = DistanceSquared(anchor.TL, current.TL);
        var tr = DistanceSquared(anchor.TR, current.TR);
        var br = DistanceSquared(anchor.BR, current.BR);
        var bl = DistanceSquared(anchor.BL, current.BL);

        return Math.Max(Math.Max(tl, tr), Math.Max(br, bl));
    }

    private static double DistanceSquared(PointF2 a, PointF2 b)
    {
        var dx = (double)a.X - b.X;
        var dy = (double)a.Y - b.Y;
        return (dx * dx) + (dy * dy);
    }

    private static PointF2 Centroid(CardQuad quad) =>
        new(
            (quad.TL.X + quad.TR.X + quad.BR.X + quad.BL.X) / 4f,
            (quad.TL.Y + quad.TR.Y + quad.BR.Y + quad.BL.Y) / 4f);
}
