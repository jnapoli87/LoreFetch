using System.Reflection;
using Xunit;

namespace LoreFetch.Tests.StreamC;

// Placeholder for Stream C. LoreFetch.Capture currently has no source files
// (and so no internal to exercise via the InternalsVisibleTo grant declared
// in its .csproj) — this instead proves the project reference resolves by
// loading the built LoreFetch.Capture assembly at runtime. Stream C replaces
// this with real tests, including one that touches its first internal, and
// may delete this file.
public class PlaceholderTests
{
    [Fact]
    public void CaptureAssembly_IsReferencedAndLoadable()
    {
        var assembly = Assembly.Load("LoreFetch.Capture");
        Assert.NotNull(assembly);
    }
}
