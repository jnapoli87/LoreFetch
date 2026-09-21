using LoreFetch.Core.Abstractions;

namespace LoreFetch.Tests.Integration.EndToEnd;

/// V8's compiling stub for the real implementation set. Every method below
/// `EnsureAvailable` is unreachable today: the concrete types this set will
/// eventually construct —
/// `LoreFetch.Core.Identification.HashCardIdentifier`,
/// `LoreFetch.Core.Imaging.ContourCardDetector`,
/// `LoreFetch.Core.Imaging.PerspectiveRectifier`,
/// `LoreFetch.Capture.WebcamFrameSourceFactory`,
/// `LoreFetch.Core.Collection.CsvCollectionStore`
/// (docs/orchestration-plan.md §4, "Concrete names fixed for integration") —
/// do not exist until streams B, C and D merge. Code referencing them here
/// would not compile, and code that does not compile cannot be skipped —
/// which is the whole reason this class is a stub rather than the real
/// wiring (Stream 0 cannot write it).
///
/// Fill in at integration (I1–I3): replace each Create* body with the real
/// construction call and delete the corresponding `NotWired()` throw.
/// `EnsureAvailable` should then check for whichever artifact a given case
/// actually needs (the hash index, the thresholds file, a live camera, an
/// on-disk collection path) rather than gating unconditionally.
public sealed class RealImplementationSet : IImplementationSet
{
    public string DisplayName => "Real";

    public void EnsureAvailable() => RealArtifactGate.SkipOrFail(
        "Real implementation set is not wired yet: streams B (HashCardIdentifier, ContourCardDetector, " +
        "PerspectiveRectifier), C (WebcamFrameSourceFactory) and D (CsvCollectionStore) have not merged " +
        "into main. See docs/orchestration-plan.md §4.");

    public ICardDetector CreateDetector(int cardCount) => throw NotWired();

    public IRectifier CreateRectifier() => throw NotWired();

    public ICardIdentifier CreateIdentifier(IReadOnlyList<int> distances) => throw NotWired();

    public IOracleCatalog CreateOracleCatalog() => throw NotWired();

    public ICollectionStore CreateCollectionStore() => throw NotWired();

    public IFrameSourceFactory CreateFrameSourceFactory(string frameFolder, TimeSpan pollInterval) => throw NotWired();

    private static InvalidOperationException NotWired() => new(
        "RealImplementationSet.EnsureAvailable() must be called — and must return — before any Create* " +
        "method. Every end-to-end test case does this first, so this path is never reached today; " +
        "reaching it is a test-authoring bug, not an expected skip.");
}
