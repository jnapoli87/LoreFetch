using System.Text.Json;
using LoreFetch.Core.Identification;
using LoreFetch.Lab;
using LoreFetch.Lab.Bulk;
using LoreFetch.Lab.Images;
using LoreFetch.Tests.StreamB.Index;
using Xunit;

namespace LoreFetch.Tests.StreamB;

/// CLI-level tests for `build-index`: argument handling, the missing-image
/// fail/--allow-missing gate, --subset/--ids selection, the console output
/// a maintainer actually reads, and the sidecar JSON the orchestrator's B4d
/// package consumes. `IndexBuilderTests` and `AtomicIndexWriterTests` cover
/// the hashing and atomic-write mechanics this command composes.
public sealed class BuildIndexCommandTests : IDisposable
{
    private readonly string _dir = IndexBuildFixtures.NewTempCacheDir();

    public void Dispose() => IndexBuildFixtures.DeleteQuietly(_dir);

    private (string ManifestPath, string CacheDir) SetUpManifestAndCache(int count = 6)
    {
        var cacheDir = Path.Combine(_dir, "cache");
        var (entries, _) = IndexBuildFixtures.CreateCacheAndManifest(cacheDir, count);
        var manifestPath = Path.Combine(_dir, "manifest.jsonl");
        FilteredArtworkManifest.Write(manifestPath, entries);
        return (manifestPath, cacheDir);
    }

    private static async Task<(int ExitCode, string Output)> RunCapturingOutputAsync(
        string[] args, Action<string, IReadOnlyList<HashIndexEntry>>? writeIndex = null)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var writer = new StringWriter();
        Console.SetOut(writer);
        Console.SetError(writer);
        try
        {
            var exitCode = await BuildIndexCommand.RunAsync(args, writeIndex);
            return (exitCode, writer.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }

    [Fact]
    public async Task FullBuild_Succeeds_WritesIndexAndSidecar()
    {
        var (manifestPath, cacheDir) = SetUpManifestAndCache(count: 6);
        var outPath = Path.Combine(_dir, "cards.lfidx");

        var (exitCode, output) = await RunCapturingOutputAsync(
            ["--manifest", manifestPath, "--cache", cacheDir, "--out", outPath]);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(outPath));

        var data = HashIndexFile.Read(outPath);
        Assert.Equal(6, data.Entries.Count);

        var sidecarPath = outPath + ".build.json";
        Assert.True(File.Exists(sidecarPath));
        using var doc = JsonDocument.Parse(File.ReadAllText(sidecarPath));
        Assert.Equal(6, doc.RootElement.GetProperty("artworkCount").GetInt32());
        Assert.Equal(HashIndexFile.ComputeSha256(outPath), doc.RootElement.GetProperty("sha256").GetString());
        Assert.Equal("full", doc.RootElement.GetProperty("mode").GetString());

        Assert.Contains("Artworks: 6", output);
        Assert.Contains("SHA-256:", output);
    }

    [Fact]
    public async Task MissingImage_WithoutAllowMissing_FailsAndWritesNothing()
    {
        var (manifestPath, cacheDir) = SetUpManifestAndCache(count: 5);
        var entries = FilteredArtworkManifest.Read(manifestPath);
        File.Delete(ImageCache.GetImagePath(cacheDir, entries[1].ArtworkId));
        var outPath = Path.Combine(_dir, "cards.lfidx");

        var (exitCode, output) = await RunCapturingOutputAsync(
            ["--manifest", manifestPath, "--cache", cacheDir, "--out", outPath]);

        Assert.Equal(1, exitCode);
        Assert.False(File.Exists(outPath));
        Assert.Contains(entries[1].ArtworkId, output);
        Assert.Contains("--allow-missing", output);
    }

    [Fact]
    public async Task MissingImage_WithAllowMissing_SkipsAndSucceeds()
    {
        var (manifestPath, cacheDir) = SetUpManifestAndCache(count: 5);
        var entries = FilteredArtworkManifest.Read(manifestPath);
        File.Delete(ImageCache.GetImagePath(cacheDir, entries[1].ArtworkId));
        var outPath = Path.Combine(_dir, "cards.lfidx");

        var (exitCode, output) = await RunCapturingOutputAsync(
            ["--manifest", manifestPath, "--cache", cacheDir, "--out", outPath, "--allow-missing"]);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(outPath));
        var data = HashIndexFile.Read(outPath);
        Assert.Equal(4, data.Entries.Count);

        var sidecarPath = outPath + ".build.json";
        using var doc = JsonDocument.Parse(File.ReadAllText(sidecarPath));
        Assert.Equal(1, doc.RootElement.GetProperty("missingCount").GetInt32());
        Assert.True(doc.RootElement.GetProperty("allowMissing").GetBoolean());
        Assert.Contains("skipping 1 missing", output);
    }

    [Fact]
    public async Task Subset_BuildsOnlyTheFirstNAndLabelsIt()
    {
        var (manifestPath, cacheDir) = SetUpManifestAndCache(count: 10);
        var outPath = Path.Combine(_dir, "subset.lfidx");

        var (exitCode, output) = await RunCapturingOutputAsync(
            ["--manifest", manifestPath, "--cache", cacheDir, "--out", outPath, "--subset", "3"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("SUBSET index: 3 of 10 arts", output);

        var data = HashIndexFile.Read(outPath);
        Assert.Equal(3, data.Entries.Count);

        var sidecarPath = outPath + ".build.json";
        using var doc = JsonDocument.Parse(File.ReadAllText(sidecarPath));
        Assert.Equal("subset:3", doc.RootElement.GetProperty("mode").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("selectedCount").GetInt32());
        Assert.Equal(10, doc.RootElement.GetProperty("manifestEntryCount").GetInt32());
    }

    [Fact]
    public async Task Ids_WithFill_BuildsNamedPlusPaddedEntries()
    {
        var (manifestPath, cacheDir) = SetUpManifestAndCache(count: 10);
        var entries = FilteredArtworkManifest.Read(manifestPath);
        var idsPath = Path.Combine(_dir, "ids.txt");
        File.WriteAllLines(idsPath, [entries[7].ArtworkId, entries[9].ArtworkId]);
        var outPath = Path.Combine(_dir, "smoke.lfidx");

        var (exitCode, _) = await RunCapturingOutputAsync(
            ["--manifest", manifestPath, "--cache", cacheDir, "--out", outPath, "--ids", idsPath, "--fill", "2"]);

        Assert.Equal(0, exitCode);
        var data = HashIndexFile.Read(outPath);
        Assert.Equal(4, data.Entries.Count); // 2 named + 2 filled
        var ids = data.Entries.Select(e => e.ArtworkId).ToHashSet();
        Assert.Contains(entries[7].ArtworkId, ids);
        Assert.Contains(entries[9].ArtworkId, ids);
    }

    [Fact]
    public async Task Ids_UnknownId_FailsAndWritesNothing()
    {
        var (manifestPath, cacheDir) = SetUpManifestAndCache(count: 4);
        var idsPath = Path.Combine(_dir, "ids.txt");
        File.WriteAllLines(idsPath, ["does-not-exist"]);
        var outPath = Path.Combine(_dir, "out.lfidx");

        var (exitCode, output) = await RunCapturingOutputAsync(
            ["--manifest", manifestPath, "--cache", cacheDir, "--out", outPath, "--ids", idsPath]);

        Assert.Equal(1, exitCode);
        Assert.False(File.Exists(outPath));
        Assert.Contains("does-not-exist", output);
    }

    [Fact]
    public async Task SubsetAndIds_AreMutuallyExclusive()
    {
        var (manifestPath, cacheDir) = SetUpManifestAndCache(count: 4);
        var idsPath = Path.Combine(_dir, "ids.txt");
        File.WriteAllLines(idsPath, ["x"]);

        var (exitCode, output) = await RunCapturingOutputAsync(
            ["--manifest", manifestPath, "--cache", cacheDir, "--out", Path.Combine(_dir, "o.lfidx"),
                "--subset", "1", "--ids", idsPath]);

        Assert.Equal(1, exitCode);
        Assert.Contains("mutually exclusive", output);
    }

    [Fact]
    public async Task MissingRequiredFlags_FailCleanly()
    {
        var (exitCodeNoCache, outputNoCache) = await RunCapturingOutputAsync(["--out", "x.lfidx"]);
        Assert.Equal(1, exitCodeNoCache);
        Assert.Contains("--cache", outputNoCache);

        var (exitCodeNoOut, outputNoOut) = await RunCapturingOutputAsync(["--cache", _dir]);
        Assert.Equal(1, exitCodeNoOut);
        Assert.Contains("--out", outputNoOut);
    }

    [Fact]
    public async Task CacheDirectoryMissing_FailsCleanly()
    {
        var (manifestPath, _) = SetUpManifestAndCache(count: 2);

        var (exitCode, output) = await RunCapturingOutputAsync(
            ["--manifest", manifestPath, "--cache", Path.Combine(_dir, "does-not-exist"), "--out", Path.Combine(_dir, "o.lfidx")]);

        Assert.Equal(1, exitCode);
        Assert.Contains("cache directory not found", output);
    }

    [Fact]
    public async Task ManifestMissing_FailsCleanly()
    {
        var (exitCode, output) = await RunCapturingOutputAsync(
            ["--manifest", Path.Combine(_dir, "nope.jsonl"), "--cache", _dir, "--out", Path.Combine(_dir, "o.lfidx")]);

        Assert.Equal(1, exitCode);
        Assert.Contains("manifest not found", output);
    }

    [Fact]
    public async Task AtomicWriteFailure_ThroughTheCommand_LeavesPreexistingOutUntouched()
    {
        var (manifestPath, cacheDir) = SetUpManifestAndCache(count: 3);
        var outPath = Path.Combine(_dir, "cards.lfidx");
        File.WriteAllText(outPath, "OLD CONTENT");

        void FaultyWrite(string tempPath, IReadOnlyList<HashIndexEntry> _)
        {
            File.WriteAllText(tempPath, "GARBAGE");
            throw new InvalidOperationException("simulated disk failure");
        }

        var (exitCode, output) = await RunCapturingOutputAsync(
            ["--manifest", manifestPath, "--cache", cacheDir, "--out", outPath], FaultyWrite);

        Assert.Equal(1, exitCode);
        Assert.Equal("OLD CONTENT", File.ReadAllText(outPath));
        Assert.Contains("failed to write", output);
    }

    [Fact]
    public async Task Rebuild_OverwritesPreviousIndex_ReadBackMatchesNewContent()
    {
        var (manifestPath, cacheDir) = SetUpManifestAndCache(count: 5);
        var outPath = Path.Combine(_dir, "cards.lfidx");

        var (firstExit, _) = await RunCapturingOutputAsync(
            ["--manifest", manifestPath, "--cache", cacheDir, "--out", outPath, "--subset", "2"]);
        Assert.Equal(0, firstExit);
        Assert.Equal(2, HashIndexFile.Read(outPath).Entries.Count);

        var (secondExit, _) = await RunCapturingOutputAsync(
            ["--manifest", manifestPath, "--cache", cacheDir, "--out", outPath]);
        Assert.Equal(0, secondExit);
        Assert.Equal(5, HashIndexFile.Read(outPath).Entries.Count);
    }
}
