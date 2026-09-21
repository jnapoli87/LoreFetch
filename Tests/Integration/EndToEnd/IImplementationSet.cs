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
/// other than `EnsureAvailable` assumes `EnsureAvailable` already returned —
/// true for `Fakes` (a no-op) and exactly the gate `Real` uses to skip
/// before reaching code that doesn't exist yet.
public interface IImplementationSet
{
    /// For test output — e.g. "Fakes", "Real".
    string DisplayName { get; }

    /// Every test case calls this FIRST, before any Create* method.
    /// `Fakes` returns immediately. `Real` skips (or fails) via
    /// `RealArtifactGate` and never returns today, so nothing after this
    /// call is ever reached by the Real set.
    void EnsureAvailable();

    ICardDetector CreateDetector(int cardCount);

    IRectifier CreateRectifier();

    /// `distances` becomes the ranked candidates' Hamming distances, one per
    /// candidate, ascending — the same shape `ICardIdentifier.Identify` is
    /// contracted to return. An empty list means "no match".
    ICardIdentifier CreateIdentifier(IReadOnlyList<int> distances);

    IOracleCatalog CreateOracleCatalog();

    ICollectionStore CreateCollectionStore();

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
