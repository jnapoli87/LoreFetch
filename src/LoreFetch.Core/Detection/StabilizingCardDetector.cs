using LoreFetch.Core.Abstractions;

namespace LoreFetch.Core.Detection;

/// Tunable knobs for `StabilizingCardDetector`. Counts are in frames, not
/// milliseconds: at the camera's 30 fps, 5 frames is ~170 ms and 8 frames
/// is ~270 ms.
public sealed record CardStabilizerOptions
{
    /// A card is reported once the inner detector has found it in at least
    /// `ConfirmHits` of the last `ConfirmWindow` frames. One-frame junk never
    /// reaches that, so the stabilizer rejects it rather than showing it.
    public int ConfirmHits { get; init; } = 3;

    public int ConfirmWindow { get; init; } = 5;

    /// A confirmed card keeps being reported, at its last-seen quad, through
    /// up to this many consecutive frames where the inner detector misses
    /// it. One more miss drops it.
    public int HoldFrames { get; init; } = 8;

    /// A detection belongs to a tracked card when their centres are closer
    /// than this fraction of the tracked card's short side. Half a short side
    /// cannot reach a neighbour in a 3x3 grid, whose centres sit at least one
    /// full short side apart.
    public float MatchRadiusFraction { get; init; } = 0.5f;

    public static CardStabilizerOptions Default { get; } = new();
}

/// Wraps another `ICardDetector` and smooths its output over time, so a card
/// the inner detector finds on some frames and misses on others stops
/// blinking on and off.
///
/// Each frame, every detection is matched to a tracked card by centre
/// distance. A tracked card is reported once it has been seen in
/// `ConfirmHits` of the last `ConfirmWindow` frames, and stays reported
/// through up to `HoldFrames` consecutive misses. While a card is being
/// seen, its newest quad is passed through unchanged; the held quad is used
/// only on frames where the inner detector missed it. Capture rectifies
/// these quads, so they must follow the card, not lag behind it.
///
/// The cost is latency at both ends: a new card appears `ConfirmHits - 1`
/// frames late, and a removed card lingers for `HoldFrames` frames.
///
/// Stateful and not thread-safe: `ScanPipeline` calls `Detect` from its one
/// loop thread, which is the only caller this is built for.
public sealed class StabilizingCardDetector : ICardDetector
{
    private readonly ICardDetector _inner;
    private readonly CardStabilizerOptions _options;
    private readonly List<Track> _tracks = [];

    public StabilizingCardDetector(ICardDetector inner, CardStabilizerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(inner);

        _inner = inner;
        _options = options ?? CardStabilizerOptions.Default;

        if (_options.ConfirmWindow is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(options), _options.ConfirmWindow, "ConfirmWindow must be between 1 and 32.");
        }

        if (_options.ConfirmHits < 1 || _options.ConfirmHits > _options.ConfirmWindow)
        {
            throw new ArgumentOutOfRangeException(nameof(options), _options.ConfirmHits, "ConfirmHits must be between 1 and ConfirmWindow.");
        }
    }

    public IReadOnlyList<CardQuad> Detect(CameraFrame frame, int maxCards)
    {
        var detections = _inner.Detect(frame, maxCards);
        var matchedTrackFor = MatchDetectionsToTracks(detections);

        var seenThisFrame = new bool[_tracks.Count];
        for (var d = 0; d < detections.Count; d++)
        {
            var t = matchedTrackFor[d];
            if (t >= 0)
            {
                _tracks[t].Seen(detections[d]);
                seenThisFrame[t] = true;
            }
        }

        // Iterate backwards so removals do not shift unvisited indices.
        for (var t = _tracks.Count - 1; t >= 0; t--)
        {
            if (seenThisFrame[t])
            {
                continue;
            }

            var track = _tracks[t];
            track.Missed();
            // A confirmed card is dropped once the hold runs out; an unconfirmed
            // one as soon as its last hit slides out of the window.
            if (track.ConsecutiveMisses > _options.HoldFrames
                || (!track.Confirmed && track.HitsInWindow(_options.ConfirmWindow) == 0))
            {
                _tracks.RemoveAt(t);
            }
        }

        // New tracks are added after the miss pass, so a detection seen for
        // the first time this frame is not also counted as missed.
        for (var d = 0; d < detections.Count; d++)
        {
            if (matchedTrackFor[d] < 0)
            {
                var track = new Track();
                track.Seen(detections[d]);
                _tracks.Add(track);
            }
        }

        foreach (var track in _tracks)
        {
            if (!track.Confirmed && track.HitsInWindow(_options.ConfirmWindow) >= _options.ConfirmHits)
            {
                track.Confirmed = true;
            }
        }

        // ICardDetector's contract: at most maxCards, descending area.
        return _tracks
            .Where(track => track.Confirmed)
            .Select(track => track.Quad)
            .OrderByDescending(quad => quad.AreaPx)
            .Take(maxCards)
            .ToList();
    }

    /// Returns, for each detection, the index of the track it continues, or
    /// -1 for a new card. Greedy closest-pair-first: with at most nine cards
    /// a globally optimal assignment buys nothing, because neighbouring
    /// cards are a full card apart and the match radius is half of one.
    private int[] MatchDetectionsToTracks(IReadOnlyList<CardQuad> detections)
    {
        var pairs = new List<(int Detection, int Track, float Distance)>();
        for (var d = 0; d < detections.Count; d++)
        {
            var centre = Centre(detections[d]);
            for (var t = 0; t < _tracks.Count; t++)
            {
                var trackQuad = _tracks[t].Quad;
                var distance = Distance(centre, Centre(trackQuad));
                if (distance < _options.MatchRadiusFraction * ShortSide(trackQuad))
                {
                    pairs.Add((d, t, distance));
                }
            }
        }

        var matchedTrackFor = new int[detections.Count];
        Array.Fill(matchedTrackFor, -1);
        var trackTaken = new bool[_tracks.Count];

        foreach (var (d, t, _) in pairs.OrderBy(pair => pair.Distance))
        {
            if (matchedTrackFor[d] < 0 && !trackTaken[t])
            {
                matchedTrackFor[d] = t;
                trackTaken[t] = true;
            }
        }

        return matchedTrackFor;
    }

    private static PointF2 Centre(CardQuad q) =>
        new((q.TL.X + q.TR.X + q.BR.X + q.BL.X) / 4f, (q.TL.Y + q.TR.Y + q.BR.Y + q.BL.Y) / 4f);

    private static float ShortSide(CardQuad q)
    {
        var width = (Distance(q.TL, q.TR) + Distance(q.BL, q.BR)) / 2f;
        var height = (Distance(q.TL, q.BL) + Distance(q.TR, q.BR)) / 2f;
        return MathF.Min(width, height);
    }

    private static float Distance(PointF2 a, PointF2 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }

    private sealed class Track
    {
        /// One bit per frame, newest in bit 0: set when the inner detector
        /// found this card on that frame.
        private uint _history;

        public CardQuad Quad { get; private set; }

        public bool Confirmed { get; set; }

        public int ConsecutiveMisses { get; private set; }

        public void Seen(CardQuad quad)
        {
            _history = (_history << 1) | 1u;
            Quad = quad;
            ConsecutiveMisses = 0;
        }

        public void Missed()
        {
            _history <<= 1;
            ConsecutiveMisses++;
        }

        public int HitsInWindow(int window)
        {
            var mask = window >= 32 ? uint.MaxValue : (1u << window) - 1;
            return System.Numerics.BitOperations.PopCount(_history & mask);
        }
    }
}
