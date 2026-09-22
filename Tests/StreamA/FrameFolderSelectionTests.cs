using LoreFetch.App;
using LoreFetch.Core.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LoreFetch.Tests.StreamA;

/// Unit tests for <see cref="AppComposition.ChooseFrameFolder"/> — the three
/// production code paths exercised without launching the full composition stack:
///
///   1. Env var points at a folder that exists and has image files → use it,
///      isTempFolder=false (never delete the user's data).
///   2. Env var points at a non-existent folder → fall back to DemoFrames,
///      isTempFolder=true.
///   3a. Env var points at a folder that exists but is empty → fall back to DemoFrames,
///       isTempFolder=true.
///   3b. Env var points at a folder that exists but contains only non-image files
///       (e.g. only a .txt) → fall back to DemoFrames, isTempFolder=true.
///       This exercises the <c>IsImageFile</c> rejection branch, which the empty-folder
///       case skips entirely (the <c>Any(IsImageFile)</c> call never runs on an empty
///       enumeration).
///   4. Env var is null or empty → fall back to DemoFrames, isTempFolder=true.
///
/// Chaos-tested: removing the <c>return (envVar, isTempFolder: false);</c>
/// branch causes <c>UserFolder_WithImages_ReturnsThatFolderAsNonTemp</c> to
/// fail: <c>folder</c> becomes a DemoFrames temp path rather than
/// <c>envVar</c>, so <c>Assert.Equal(userFolder, folder)</c> fails with the
/// wrong path — the right assertion, at the right line, for the right bug.
public class FrameFolderSelectionTests : IDisposable
{
    private readonly ILogger _logger = NullLoggerFactory.Instance.CreateLogger("test");

    // Folders this test created that need cleanup on test teardown, in addition
    // to any DemoFrames folders returned by ChooseFrameFolder in fallback paths.
    private readonly List<string> _toDelete = [];

    [Fact]
    public void UserFolder_WithImages_ReturnsThatFolderAsNonTemp()
    {
        var userFolder = MakeTempDir();
        File.WriteAllBytes(Path.Combine(userFolder, "card.png"), [0x89, 0x50, 0x4E, 0x47]);

        var (folder, isTempFolder) = AppComposition.ChooseFrameFolder(userFolder, _logger);

        // The returned folder must be the user's own folder (not a DemoFrames
        // temp folder), and it must never be flagged for deletion.
        Assert.Equal(userFolder, folder);
        Assert.False(isTempFolder, "User-supplied folder must never be flagged as temp.");
    }

    [Fact]
    public void UserFolder_Missing_FallsBackToDemoFrames()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        // Intentionally not created on disk.

        var (folder, isTempFolder) = AppComposition.ChooseFrameFolder(missingPath, _logger);
        _toDelete.Add(folder); // DemoFrames temp folder — clean up after test

        Assert.True(isTempFolder, "Fallback DemoFrames folder must be flagged as temp.");
        Assert.True(Directory.Exists(folder), "Fallback folder must exist on disk.");
        Assert.NotEqual(missingPath, folder);
    }

    [Fact]
    public void UserFolder_ExistsButEmpty_FallsBackToDemoFrames()
    {
        var emptyFolder = MakeTempDir(); // exists, but no files in it

        var (folder, isTempFolder) = AppComposition.ChooseFrameFolder(emptyFolder, _logger);
        _toDelete.Add(folder); // DemoFrames temp folder — clean up after test

        Assert.True(isTempFolder, "Fallback DemoFrames folder must be flagged as temp.");
        Assert.True(Directory.Exists(folder), "Fallback folder must exist on disk.");
        Assert.NotEqual(emptyFolder, folder);
    }

    [Fact]
    public void UserFolder_WithOnlyNonImageFiles_FallsBackToDemoFrames()
    {
        // Folder exists and has a file — but the file is not a recognised image
        // extension. This exercises the IsImageFile rejection branch: the folder
        // is non-empty so the empty-folder short-circuit doesn't apply; IsImageFile
        // must return false for every entry for the fallback to trigger.
        var userFolder = MakeTempDir();
        File.WriteAllText(Path.Combine(userFolder, "notes.txt"), "these are not frames");

        var (folder, isTempFolder) = AppComposition.ChooseFrameFolder(userFolder, _logger);
        _toDelete.Add(folder); // DemoFrames temp folder — clean up after test

        Assert.True(isTempFolder, "Fallback DemoFrames folder must be flagged as temp.");
        Assert.True(Directory.Exists(folder), "Fallback folder must exist on disk.");
        Assert.NotEqual(userFolder, folder);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NullOrEmptyEnvVar_FallsBackToDemoFrames(string? envVar)
    {
        var (folder, isTempFolder) = AppComposition.ChooseFrameFolder(envVar, _logger);
        _toDelete.Add(folder); // DemoFrames temp folder — clean up after test

        Assert.True(isTempFolder, "DemoFrames fallback must be flagged as temp.");
        Assert.True(Directory.Exists(folder), "Fallback folder must exist on disk.");
    }

    // Clean up every temp folder this test created, including any DemoFrames
    // folders the fallback path generated. Done in Dispose rather than in each
    // test so a failed assertion still runs cleanup.
    public void Dispose()
    {
        foreach (var dir in _toDelete)
        {
            DemoFrames.DeleteFolderBestEffort(dir);
        }
    }

    private string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        _toDelete.Add(dir);
        return dir;
    }
}
