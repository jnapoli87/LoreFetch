using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Fakes;

namespace LoreFetch.Tests.Integration.EndToEnd;

/// Wires the fakes in `src/LoreFetch.Core/Fakes/` (CONTRACTS.md §"The seven
/// fakes" — reconciliation added an eighth, `StubCollectionExporter`, which
/// this set does not expose because no S0.6a/S0.6b case exports anything).
/// Fully working from the end of Stream 0, so `EnsureAvailable` is a no-op.
public sealed class FakeImplementationSet : IImplementationSet
{
    public string DisplayName => "Fakes";

    public void EnsureAvailable()
    {
        // Always ready — the fakes exist today.
    }

    public void EnsureCollectionAvailable()
    {
        // Always ready — StubCollectionStore and StubCollectionExporter exist today.
    }

    public ICardDetector CreateDetector(int cardCount) => new StubCardDetector(cardCount);

    public IRectifier CreateRectifier() => new StubRectifier();

    public ICardIdentifier CreateIdentifier(IReadOnlyList<int> distances) =>
        new StubCardIdentifier { NextDistances = distances };

    public IOracleCatalog CreateOracleCatalog() => new StubOracleCatalog();

    public ICollectionStore CreateCollectionStore(string path) => new StubCollectionStore();

    public IReadOnlyList<ICollectionExporter> CreateExporters() =>
    [
        new StubCollectionExporter(new ExportFormat("stub-a", "Stub Verified Export", ".txt", IsVerified: true, Notes: null)),
        new StubCollectionExporter(new ExportFormat("stub-b", "Stub Unverified Export", ".txt", IsVerified: false, Notes: "Not yet tested with a live tool.")),
    ];

    public IFrameSourceFactory CreateFrameSourceFactory(string frameFolder, TimeSpan pollInterval) =>
        new FolderFrameSourceFactory(frameFolder, pollInterval);
}
