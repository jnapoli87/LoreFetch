using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Collection;
using LoreFetch.Core.Export;
using Microsoft.Extensions.Logging.Abstractions;

namespace LoreFetch.Tests.Integration.EndToEnd;

/// V8's compiling stub for the real implementation set. Package I1 wires in
/// the collection store and both export adapters — `EnsureCollectionAvailable`
/// is now a no-op and `CreateCollectionStore`/`CreateExporters` construct the
/// real classes. Every OTHER method below `EnsureAvailable` is still
/// unreachable today: the concrete types this set will eventually construct —
/// `LoreFetch.Core.Identification.HashCardIdentifier`,
/// `LoreFetch.Core.Imaging.ContourCardDetector`,
/// `LoreFetch.Core.Imaging.PerspectiveRectifier`,
/// `LoreFetch.Capture.WebcamFrameSourceFactory`
/// (docs/orchestration-plan.md §4, "Concrete names fixed for integration") —
/// do not exist until streams B and C merge. Code referencing them here
/// would not compile, and code that does not compile cannot be skipped —
/// which is the whole reason those four still throw rather than construct.
///
/// Fill in the rest at integration (I2–I3): replace each remaining Create*
/// body with the real construction call and delete the corresponding
/// `NotWired()` throw, and narrow `EnsureAvailable`'s reason as each
/// artifact (the hash index, the thresholds file, a live camera) lands.
public sealed class RealImplementationSet : IImplementationSet
{
    public string DisplayName => "Real";

    public void EnsureAvailable() => RealArtifactGate.SkipOrFail(
        "Real implementation set's detector/rectifier/identifier/frame-source slots are not wired yet: " +
        "streams B (HashCardIdentifier, ContourCardDetector, PerspectiveRectifier) and C " +
        "(WebcamFrameSourceFactory) have not merged into main. See docs/orchestration-plan.md §4. " +
        "(The collection store and export adapters ARE wired — a case that needs only those should call " +
        "EnsureCollectionAvailable() instead.)");

    /// No-op as of package I1: `CsvCollectionStore`, `NativeCsvExporter` and
    /// `MoxfieldCsvExporter` are real. Nothing to gate.
    public void EnsureCollectionAvailable()
    {
    }

    public ICardDetector CreateDetector(int cardCount) => throw NotWired();

    public IRectifier CreateRectifier() => throw NotWired();

    public ICardIdentifier CreateIdentifier(IReadOnlyList<int> distances) => throw NotWired();

    public IOracleCatalog CreateOracleCatalog() => throw NotWired();

    public ICollectionStore CreateCollectionStore(string path) =>
        new CsvCollectionStore(path, NullLogger<CsvCollectionStore>.Instance);

    public IReadOnlyList<ICollectionExporter> CreateExporters() =>
    [
        new NativeCsvExporter(),
        new MoxfieldCsvExporter(),
    ];

    public IFrameSourceFactory CreateFrameSourceFactory(string frameFolder, TimeSpan pollInterval) => throw NotWired();

    private static InvalidOperationException NotWired() => new(
        "RealImplementationSet.EnsureAvailable() must be called — and must return — before any Create* " +
        "method that needs the detector, rectifier, identifier or frame source. Every end-to-end test " +
        "case that touches those does this first, so this path is never reached today; reaching it is a " +
        "test-authoring bug, not an expected skip.");
}
