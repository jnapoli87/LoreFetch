namespace LoreFetch.Lab.Accuracy;

/// Tunable knobs for the B6 accuracy harness. Every default is documented
/// as either a literature prior or a deliberately conservative placeholder
/// -- NOT a value calibrated from the real H3 corpus, which does not exist
/// yet (this package builds and tests the harness; the corpus is being
/// captured separately). See each field's own comment.
public sealed record AccuracyHarnessOptions
{
    /// The distance at or below which a rank-1 result counts as "confident"
    /// for the wrong@1 classification (DECISIONS.md/CONTEXT.md's own "Ok
    /// threshold": "between good and ok is a low-confidence match").
    ///
    /// Default 270 -- CardSpotter upstream's own `myOkMatchScore` default
    /// (docs/design/identification.md "Verified", target 1: `QueryThread.cpp
    /// :152-153`, `myOkMatchScore(270)`). This is NOT a value calibrated
    /// against LoreFetch's own margin data -- there is no real-corpus
    /// margin data yet (H3 gates B6) -- it is a documented, cited prior
    /// carried over until the real corpus can calibrate `OkDistance`
    /// properly (see `ThresholdsCalibration`, which computes a suggested
    /// value FROM a run's own statistics but is never invoked by this
    /// package against synthetic data).
    public int OkDistance { get; init; } = 270;

    /// The `AccuracyGate`'s run-fails-if bound: the run fails when the
    /// headline (non-land, normal-rung) wrong@1 count exceeds this many, at
    /// `OkDistance`.
    ///
    /// Default 0 -- zero tolerance. Chosen deliberately, not arbitrarily:
    /// DECISIONS.md's own reasoning is "a silent miss is recoverable, a
    /// confident wrong answer is permanent bad inventory" (TESTING.md
    /// "Accuracy"), and with no real corpus yet to justify a looser bound
    /// empirically, the conservative default is the one that fails the
    /// build rather than the collection. Raise this once real H3 data shows
    /// a nonzero count is actually expected at this `OkDistance` -- do not
    /// raise it to make a run pass.
    public int MaxWrongAt1AtOkDistance { get; init; }

    /// How many ranked candidates `ICardIdentifier.Identify` is asked for
    /// per slot. Must be at least 2 for the margin distribution (rank-1 vs
    /// the best DIFFERENT `OracleId`) to be computable at all; 3 leaves
    /// headroom without meaningfully changing brute-force cost (DECISIONS.md:
    /// 0.243 ms/query either way).
    public int MaxCandidates { get; init; } = 3;

    public static AccuracyHarnessOptions Default { get; } = new();
}
