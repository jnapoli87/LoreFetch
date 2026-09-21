using LoreFetch.Core.Abstractions;

namespace LoreFetch.Core.Trigger;

/// The count-gated settle trigger (CLAUDE.md "Interaction — capture, then
/// accept the cohort"): fires once the exact expected count has been held
/// stable for `ScanSettings.SettleMilliseconds`, then refuses to fire again
/// until "the scene breaks" — any snapshot whose quad count differs from
/// `expectedCount`, zero included (docs/stream-a-ui.md A0). Without that
/// re-arm rule a static tableau would re-fire every settle window forever.
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

    /// The previous snapshot's quads, kept ONLY while that snapshot already
    /// matched `expectedCount` — used solely to detect movement between
    /// consecutive matching snapshots. Cleared whenever the count doesn't
    /// match, so the next matching snapshot starts a fresh settle window
    /// instead of comparing across a gap.
    private IReadOnlyList<CardQuad>? _previousQuads;

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

        // "The scene breaks" — defined in docs/stream-a-ui.md A0 as any
        // count mismatch, zero included. Breaking always re-arms and always
        // restarts the settle window; there is nothing "stable" to measure
        // across a count change.
        if (quads.Count != expectedCount)
        {
            _armed = true;
            _settleStart = null;
            _previousQuads = null;
            return false;
        }

        if (_settleStart is null || _previousQuads is null || HasMovedBeyondTolerance(_previousQuads, quads))
        {
            // First matching snapshot after a break, or the cards shifted
            // beyond ε since the last one: (re)start the settle window.
            // Movement resets the TIMER but is deliberately NOT a scene
            // break, so it never re-arms an already-fired trigger — only a
            // count mismatch does that.
            _settleStart = now;
            _previousQuads = quads;
            return false;
        }

        _previousQuads = quads;

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
        _previousQuads = null;
    }

    /// Matches `current` quads to `previous` quads by nearest centroid —
    /// NOT by list index. `ICardDetector` orders by descending area, and two
    /// cards of near-identical area can swap places between frames; matching
    /// by index would read that swap as movement and reset the settle timer
    /// forever (docs/stream-a-ui.md A0). Both lists are already known to be
    /// the same length: both equal `expectedCount` at the call site.
    ///
    /// Greedy nearest-available matching rather than a globally optimal
    /// assignment: for the small counts here (1, 3 or 9) a simple ordered
    /// swap — the only case that actually occurs frame-to-frame — is
    /// resolved correctly by greedy matching, and an optimal assignment
    /// solver buys nothing a webcam scene will ever exercise.
    private bool HasMovedBeyondTolerance(IReadOnlyList<CardQuad> previous, IReadOnlyList<CardQuad> current)
    {
        var previousCentroids = new PointF2[previous.Count];
        for (var i = 0; i < previous.Count; i++)
        {
            previousCentroids[i] = Centroid(previous[i]);
        }

        var used = new bool[previous.Count];

        foreach (var quad in current)
        {
            var centroid = Centroid(quad);

            var bestIndex = -1;
            var bestDistanceSquared = double.MaxValue;

            for (var i = 0; i < previousCentroids.Length; i++)
            {
                if (used[i])
                {
                    continue;
                }

                var dx = (double)centroid.X - previousCentroids[i].X;
                var dy = (double)centroid.Y - previousCentroids[i].Y;
                var distanceSquared = (dx * dx) + (dy * dy);

                if (distanceSquared < bestDistanceSquared)
                {
                    bestDistanceSquared = distanceSquared;
                    bestIndex = i;
                }
            }

            // previous.Count == current.Count is guaranteed by the caller
            // (both already checked equal to expectedCount), so a free
            // previous centroid always exists here.
            used[bestIndex] = true;

            if (bestDistanceSquared > _movementToleranceSquaredPixels)
            {
                return true;
            }
        }

        return false;
    }

    private static PointF2 Centroid(CardQuad quad) =>
        new(
            (quad.TL.X + quad.TR.X + quad.BR.X + quad.BL.X) / 4f,
            (quad.TL.Y + quad.TR.Y + quad.BR.Y + quad.BL.Y) / 4f);
}
