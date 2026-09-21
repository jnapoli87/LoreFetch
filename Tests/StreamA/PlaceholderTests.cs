using Xunit;

namespace LoreFetch.Tests.StreamA;

// Placeholder for Stream A. Proves the project references to LoreFetch.App and
// LoreFetch.Core resolve, so this project builds a real dependency graph rather
// than an empty shell. Stream A replaces this with real tests and may delete it.
public class PlaceholderTests
{
    [Fact]
    public void AppAndCoreTypesAreReachable()
    {
        Assert.Equal("LoreFetch.App.App", typeof(LoreFetch.App.App).FullName);
        Assert.NotNull(typeof(LoreFetch.Core.Abstractions.ICollectionStore));
    }
}
