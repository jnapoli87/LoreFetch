using LoreFetch.Core.Scanning;
using Xunit;

namespace LoreFetch.Tests.Integration.Unit;

/// `DataFiles` — package S0.4b, orchestration finding V11. Both paths must
/// be rooted at `AppContext.BaseDirectory` (not the current directory,
/// which differs between `dotnet run`, a test host and a published exe) and
/// end with the repo's `data/index/**` layout that `App.csproj` copies.
/// Assertions use `Path.Combine`/`Path.IsPathRooted` rather than a
/// hardcoded separator, so this passes on both macOS and Windows.
public class DataFilesTests
{
    [Fact]
    public void IndexPath_IsRootedAtBaseDirectory_AndEndsWithTheExpectedRelativePath()
    {
        var expectedSuffix = Path.Combine("data", "index", "cards.lfidx");

        Assert.True(Path.IsPathRooted(DataFiles.IndexPath));
        Assert.StartsWith(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), DataFiles.IndexPath, StringComparison.Ordinal);
        Assert.EndsWith(expectedSuffix, DataFiles.IndexPath, StringComparison.Ordinal);
    }

    [Fact]
    public void ThresholdsPath_IsRootedAtBaseDirectory_AndEndsWithTheExpectedRelativePath()
    {
        var expectedSuffix = Path.Combine("data", "index", "thresholds.json");

        Assert.True(Path.IsPathRooted(DataFiles.ThresholdsPath));
        Assert.StartsWith(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), DataFiles.ThresholdsPath, StringComparison.Ordinal);
        Assert.EndsWith(expectedSuffix, DataFiles.ThresholdsPath, StringComparison.Ordinal);
    }
}
