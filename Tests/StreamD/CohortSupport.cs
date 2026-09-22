using LoreFetch.Core.Abstractions;

namespace LoreFetch.Tests.StreamD;

/// Builds real `Cohort`/`CohortTile` objects (frozen Abstractions types,
/// not fakes) through their actual public API, so store tests exercise the
/// same state machine the pipeline does. `CohortTile`'s constructor and
/// `SetManually` are the ONLY ways to reach `Included`/`ManuallySet` with a
/// non-null `Chosen` — that invariant is enforced by the sealed class
/// itself, which is exactly what makes it safe for the store to trust.
internal static class CohortSupport
{
    private const int GoodDistance = 100;
    private const int OkDistance = 200;

    private static RectifiedCard DummyCard() => new(
        new byte[16],
        stride: 4,
        layout: PixelLayout.Bgr24,
        sourceQuad: new CardQuad(
            new PointF2(0, 0), new PointF2(1, 0), new PointF2(1, 1), new PointF2(0, 1)));

    /// A tile whose best candidate is within `GoodDistance`, so the
    /// constructor alone proposes State = Included with that candidate
    /// chosen — no extra step needed.
    public static CohortTile IncludedTile(string oracleId, string oracleName, int distance = 50, string? artworkId = "art-1") =>
        new(DummyCard(), [new CardCandidate(oracleId, oracleName, distance, artworkId)], GoodDistance, OkDistance);

    /// `SetManually` is the only path to `ManuallySet`, and it always nulls
    /// `ChosenDistance`/`ChosenArtworkId` on the tile itself — the seed
    /// candidate here exists only to give the tile *some* starting state
    /// before the manual override, and is never what ends up committed.
    public static CohortTile ManualTile(string oracleId, string oracleName)
    {
        var tile = IncludedTile(oracleId, oracleName);
        tile.SetManually(new OracleEntry(oracleId, oracleName));
        return tile;
    }

    public static CohortTile ExcludedTile(string oracleId, string oracleName)
    {
        var tile = IncludedTile(oracleId, oracleName);
        tile.ToggleExcluded();
        return tile;
    }

    /// No candidates at all → the constructor proposes Unresolved, per
    /// `CohortTile.ProposeFromHash`.
    public static CohortTile UnresolvedTile() =>
        new(DummyCard(), [], GoodDistance, OkDistance);

    public static Cohort Cohort(DateTimeOffset capturedAt, params CohortTile[] tiles) =>
        new(Guid.NewGuid(), capturedAt, tiles.Length, CaptureReason.Manual, tiles);
}
