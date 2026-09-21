namespace LoreFetch.Core.Abstractions;

public enum TileState
{
    Included,
    Excluded,
    Unresolved,
    ManuallySet,
}

public enum CaptureReason
{
    Manual,
    AutoSettle,
}

/// State transitions live on the tile, not in the UI. They are the
/// interaction model, and putting them behind methods means the UI can't
/// invent a fifth state or forget to null the distance on a manual set.
public sealed class CohortTile
{
    private readonly int _goodDistance;
    private readonly int _okDistance;

    /// Remembers which state `ToggleExcluded` came from, so toggling twice
    /// returns to the original state and not merely to Included. Only
    /// meaningful while State is Excluded.
    private TileState _preExcludeState;

    public CohortTile(
        RectifiedCard image,
        IReadOnlyList<CardCandidate> candidates,
        int goodDistance,
        int okDistance)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(candidates);

        Image = image;
        Candidates = candidates;
        _goodDistance = goodDistance;
        _okDistance = okDistance;

        (State, Chosen, ChosenDistance, IsLowConfidence) =
            ProposeFromHash(candidates, goodDistance, okDistance);
    }

    public RectifiedCard Image { get; }

    public IReadOnlyList<CardCandidate> Candidates { get; } // ranked, unfiltered

    public OracleEntry? Chosen { get; private set; } // null only when Unresolved

    public int? ChosenDistance { get; private set; } // null when ManuallySet or Unresolved

    public TileState State { get; private set; }

    public bool IsLowConfidence { get; private set; } // highlight only, never gating

    /// Included ⇄ Excluded, and ManuallySet ⇄ Excluded (remembering which).
    /// No-op on Unresolved: there is nothing to commit anyway.
    public void ToggleExcluded()
    {
        switch (State)
        {
            case TileState.Included:
            case TileState.ManuallySet:
                _preExcludeState = State;
                State = TileState.Excluded;
                break;

            case TileState.Excluded:
                State = _preExcludeState;
                break;

            case TileState.Unresolved:
                break; // no-op — nothing to commit anyway
        }
    }

    /// The user picked the card. State → ManuallySet, ChosenDistance → null.
    public void SetManually(OracleEntry card)
    {
        State = TileState.ManuallySet;
        Chosen = card;
        ChosenDistance = null;
        IsLowConfidence = false;
    }

    /// Revert a manual choice to the hash's own proposal: Included if the
    /// best candidate is within the ok threshold, otherwise Unresolved.
    /// No-op unless State is ManuallySet.
    /// The tile is constructed with the pipeline's two thresholds so it can
    /// re-apply the pipeline's own numbers here — it never sources them.
    public void Clear()
    {
        if (State != TileState.ManuallySet)
        {
            return; // no-op unless State is ManuallySet
        }

        (State, Chosen, ChosenDistance, IsLowConfidence) =
            ProposeFromHash(Candidates, _goodDistance, _okDistance);
    }

    /// The single place the initial-state rule is written. Both the
    /// constructor and `Clear()` call this, so the rule can never drift
    /// between "what a fresh tile starts as" and "what a cleared tile
    /// reverts to" — they are, by construction, the same computation.
    private static (TileState State, OracleEntry? Chosen, int? ChosenDistance, bool IsLowConfidence)
        ProposeFromHash(IReadOnlyList<CardCandidate> candidates, int goodDistance, int okDistance)
    {
        if (candidates.Count == 0)
        {
            return (TileState.Unresolved, null, null, false);
        }

        var best = candidates[0];

        if (best.Distance <= goodDistance)
        {
            return (TileState.Included, new OracleEntry(best.OracleId, best.OracleName), best.Distance, false);
        }

        if (best.Distance <= okDistance)
        {
            return (TileState.Included, new OracleEntry(best.OracleId, best.OracleName), best.Distance, true);
        }

        return (TileState.Unresolved, null, null, false);
    }
}

/// The cards from one capture. Not IDisposable — it owns no pooled memory.
public sealed class Cohort
{
    public Cohort(
        Guid id,
        DateTimeOffset capturedAt,
        int expectedCount,
        CaptureReason reason,
        IReadOnlyList<CohortTile> tiles)
    {
        ArgumentNullException.ThrowIfNull(tiles);

        Id = id;
        CapturedAt = capturedAt;
        ExpectedCount = expectedCount;
        Reason = reason;
        Tiles = tiles;
    }

    public Guid Id { get; }
    public DateTimeOffset CapturedAt { get; }
    public int ExpectedCount { get; }
    public CaptureReason Reason { get; }
    public IReadOnlyList<CohortTile> Tiles { get; }
}
