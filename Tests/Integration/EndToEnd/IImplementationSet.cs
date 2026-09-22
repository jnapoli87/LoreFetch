using LoreFetch.Core.Abstractions;
using Xunit;

namespace LoreFetch.Tests.Integration.EndToEnd;

/// Which implementation set a parameterised end-to-end test case runs
/// against (orchestration finding V8). `Fakes` is fully wired from the end
/// of Stream 0; `Real` is a compiling stub that skips — or fails, under
/// `LOREFETCH_REQUIRE_REAL` (finding V7) — until streams B, C and D land
/// and someone fills `RealImplementationSet` in at integration.
public enum ImplementationSetKind
{
    Fakes,
    Real,
}

/// One implementation of every component the end-to-end suite composes
/// through `ScanPipelineFactory.Create` and `ICollectionStore`. Every method
/// other than `EnsureAvailable`/`EnsureCollectionAvailable` assumes whichever
/// of those two a case calls has already returned — true for `Fakes` (both
/// are no-ops) and, as of package I1, true for `Real`'s
/// `EnsureCollectionAvailable` too: the store and both exporters are real
/// now, so only `EnsureAvailable` (the whole-pipeline gate — detector,
/// rectifier, identifier, frame source, all still I2/I3's) keeps skipping.
public interface IImplementationSet
{
    /// For test output — e.g. "Fakes", "Real".
    string DisplayName { get; }

    /// Every case that needs the DETECTOR, RECTIFIER, IDENTIFIER or FRAME
    /// SOURCE calls this FIRST, before any Create* method. `Fakes` returns
    /// immediately. `Real` skips (or fails) via `RealArtifactGate` and never
    /// returns today — those four slots are I2's (detector/rectifier/
    /// identifier) and I3's (frame source) — so nothing after this call is
    /// ever reached by the Real set. A case that needs ONLY the collection
    /// store and/or the exporters must call `EnsureCollectionAvailable`
    /// instead — calling this one here would wrongly gate it on pipeline
    /// pieces it never touches.
    void EnsureAvailable();

    /// Every case that needs ONLY the COLLECTION STORE and/or the EXPORT
    /// ADAPTERS — never the detector, rectifier, identifier or frame source —
    /// calls this FIRST instead of `EnsureAvailable`. `Fakes` is a no-op, like
    /// `EnsureAvailable`. `Real` is ALSO a no-op as of package I1:
    /// `CsvCollectionStore`, `NativeCsvExporter` and `MoxfieldCsvExporter` are
    /// wired, so such a case must actually run against them rather than skip.
    void EnsureCollectionAvailable();

    ICardDetector CreateDetector(int cardCount);

    IRectifier CreateRectifier();

    /// `distances` becomes the ranked candidates' Hamming distances, one per
    /// candidate, ascending — the same shape `ICardIdentifier.Identify` is
    /// contracted to return. An empty list means "no match".
    ICardIdentifier CreateIdentifier(IReadOnlyList<int> distances);

    IOracleCatalog CreateOracleCatalog();

    /// `path` is where the store keeps its data. `Fakes` ignores it entirely
    /// — `StubCollectionStore` is in-memory. `Real` opens `CsvCollectionStore`
    /// there, so give each call its own fresh path unless the case
    /// deliberately wants two stores to observe the same file.
    ICollectionStore CreateCollectionStore(string path);

    /// The registered export adapters, in the same relative order
    /// `AppComposition` registers them (one native/verified-shaped adapter,
    /// then one third-party adapter). `Fakes` returns two
    /// `StubCollectionExporter`s; `Real` returns the actual
    /// `NativeCsvExporter` and `MoxfieldCsvExporter`.
    IReadOnlyList<ICollectionExporter> CreateExporters();

    IFrameSourceFactory CreateFrameSourceFactory(string frameFolder, TimeSpan pollInterval);
}

/// Constructs an `IImplementationSet` for a given `ImplementationSetKind`,
/// and supplies the `[Theory]` data every end-to-end case is parameterised
/// over.
public static class ImplementationSets
{
    public static TheoryData<ImplementationSetKind> All => new()
    {
        ImplementationSetKind.Fakes,
        ImplementationSetKind.Real,
    };

    public static IImplementationSet Create(ImplementationSetKind kind) => kind switch
    {
        ImplementationSetKind.Fakes => new FakeImplementationSet(),
        ImplementationSetKind.Real => new RealImplementationSet(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown implementation set kind."),
    };
}
