using LoreFetch.Core.Abstractions;

namespace LoreFetch.App;

/// Holds each drawn overlay quad perfectly still until the detected card
/// has genuinely moved. Two kinds of detector noise would otherwise make a
/// border on a still card shimmer:
///
/// - Corner jitter of a pixel or two from frame to frame. A detection whose
///   four corners all lie within `TolerancePx` of the drawn quad leaves it
///   where it is.
/// - Flip-flopping between two outlines a few pixels apart. A real capture
///   of a square-on card alternated between two on almost every redraw, and
///   at a corner the two are further apart than `TolerancePx`. So a
///   detection outside the tolerance moves the border only once the card
///   has stayed outside it for `MoveAfterRenders` redraws in a row; a flip
///   back resets the count.
///
/// The comparison is always against what is on screen, never against the
/// previous detection, so a slow slide still catches up once it has moved
/// `TolerancePx` in total rather than creeping along unseen.
///
/// Display only: capture rectifies the pipeline's own quads, never these.
/// Called once per rendered preview frame, on the UI thread alone, so it
/// holds no lock.
public sealed class QuadOverlayDeadband
{
    /// In frame pixels. Above the detector's 1-3 px corner jitter and far
    /// below a hand moving a card (docs/design/app.md A0 picks the same
    /// 4 px for the auto trigger's movement tolerance, for the same reason).
    public const float TolerancePx = 4f;

    /// Redraws a detection must stay outside `TolerancePx` before the border
    /// follows it: ~200 ms at the preview's ~15 fps. Replaying the recorded
    /// flip-flop, 1 let 66 border jumps through in 13 s, 2 let 14, 3 let 4.
    public const int MoveAfterRenders = 3;

    private List<Track> _tracks = [];

    public IReadOnlyList<CardQuad> Apply(IReadOnlyList<CardQuad> quads)
    {
        ArgumentNullException.ThrowIfNull(quads);

        var next = new List<Track>(quads.Count);
        var result = new List<CardQuad>(quads.Count);
        foreach (var quad in quads)
        {
            var track = TakeNearestTrack(quad) ?? new Track(quad);
            track.Observe(quad);
            next.Add(track);
            result.Add(track.Drawn);
        }

        // Tracks no detection matched this time are dropped: presence is
        // the pipeline's StabilizingCardDetector's job, not the overlay's.
        _tracks = next;
        return result;
    }

    /// Removes and returns the track drawn closest to `quad`, if its centre
    /// is within half a short side of it -- the same card, not a neighbour.
    private Track? TakeNearestTrack(CardQuad quad)
    {
        var centre = Centre(quad);
        Track? best = null;
        var bestDistance = float.MaxValue;
        foreach (var track in _tracks)
        {
            var distance = Distance(centre, Centre(track.Drawn));
            if (distance < 0.5f * ShortSide(track.Drawn) && distance < bestDistance)
            {
                best = track;
                bestDistance = distance;
            }
        }

        if (best is not null)
        {
            _tracks.Remove(best);
        }

        return best;
    }

    private static bool WithinTolerance(CardQuad a, CardQuad b) =>
        Distance(a.TL, b.TL) <= TolerancePx && Distance(a.TR, b.TR) <= TolerancePx
        && Distance(a.BR, b.BR) <= TolerancePx && Distance(a.BL, b.BL) <= TolerancePx;

    private static PointF2 Centre(CardQuad q) =>
        new((q.TL.X + q.TR.X + q.BR.X + q.BL.X) / 4f, (q.TL.Y + q.TR.Y + q.BR.Y + q.BL.Y) / 4f);

    private static float ShortSide(CardQuad q) =>
        MathF.Min(
            (Distance(q.TL, q.TR) + Distance(q.BL, q.BR)) / 2f,
            (Distance(q.TL, q.BL) + Distance(q.TR, q.BR)) / 2f);

    private static float Distance(PointF2 a, PointF2 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }

    private sealed class Track(CardQuad drawn)
    {
        private int _rendersOutside;

        public CardQuad Drawn { get; private set; } = drawn;

        public void Observe(CardQuad detected)
        {
            if (WithinTolerance(Drawn, detected))
            {
                _rendersOutside = 0;
                return;
            }

            if (++_rendersOutside >= MoveAfterRenders)
            {
                Drawn = detected;
                _rendersOutside = 0;
            }
        }
    }
}
