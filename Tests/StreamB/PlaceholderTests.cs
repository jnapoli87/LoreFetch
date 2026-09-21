using Xunit;

namespace LoreFetch.Tests.StreamB;

// Placeholder for Stream B. Proves the project references to LoreFetch.Core and
// LoreFetch.Lab resolve, so this project builds a real dependency graph rather
// than an empty shell. Stream B replaces this with real tests and may delete it.
public class PlaceholderTests
{
    [Fact]
    public void CoreAndLabTypesAreReachable()
    {
        Assert.NotNull(typeof(LoreFetch.Core.Abstractions.ICardIdentifier));
        Assert.NotNull(typeof(LoreFetch.Core.Abstractions.RectifiedCard));
    }
}
