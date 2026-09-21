using Xunit;

namespace LoreFetch.Tests.StreamD;

// Placeholder for Stream D. Proves the project reference to LoreFetch.Core
// resolves, so this project builds a real dependency graph rather than an
// empty shell. Stream D replaces this with real tests and may delete it.
public class PlaceholderTests
{
    [Fact]
    public void CoreCollectionTypesAreReachable()
    {
        Assert.NotNull(typeof(LoreFetch.Core.Abstractions.ICollectionStore));
        Assert.NotNull(typeof(LoreFetch.Core.Abstractions.CollectionRow));
    }
}
