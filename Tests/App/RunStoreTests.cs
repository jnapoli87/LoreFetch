using LoreFetch.App;
using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Collection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LoreFetch.Tests.App;

/// <summary>
/// Issue #20: <see cref="RunStore"/>, the run the window commits to. Runs
/// over the real <see cref="CsvCollectionStore"/> in a temp folder, so
/// "open a file that isn't a run" is checked against the real native-header
/// check rather than a stub that would accept anything.
/// </summary>
public sealed class RunStoreTests : IDisposable
{
    private const int Good = 100;
    private const int Ok = 200;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lorefetch-runs-{Guid.NewGuid():N}");
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 2, 14, 30, 0, TimeSpan.Zero));

    private string RunsFolder => Path.Combine(_root, "Runs");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private RunStore NewRuns(string? startPath = null) =>
        new(RunsFolder, path => new CsvCollectionStore(path, NullLogger<CsvCollectionStore>.Instance), _clock, startPath);

    private static Cohort MakeCohort(params string[] names)
    {
        var card = new RectifiedCard([0, 0, 0, 255], stride: 4, PixelLayout.Bgra32, default);
        var tiles = names
            .Select(n => new CohortTile(card, [new CardCandidate("oracle-" + n, n, 50, ArtworkId: null)], Good, Ok))
            .ToList<CohortTile>();
        return new Cohort(Guid.NewGuid(), DateTimeOffset.UtcNow, tiles.Count, CaptureReason.Manual, tiles);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NewRun_IsNamedAfterTheLocalTime_AndWritesNothingUntilTheFirstCommit()
    {
        var runs = NewRuns();

        Assert.Equal("2026-10-02 14-30", runs.Name);
        Assert.Equal(Path.Combine(RunsFolder, "2026-10-02 14-30.csv"), runs.Path);
        Assert.Empty(await runs.ListAsync(Ct));
        Assert.False(Directory.Exists(RunsFolder), "A launch with no commits must not create the runs folder.");

        await runs.CommitCohortAsync(MakeCohort("Opt"), Ct);

        Assert.True(File.Exists(runs.Path));
        Assert.Single(await runs.ListAsync(Ct));
    }

    [Fact]
    public async Task StartNew_InTheSameMinute_GetsANumberedName_AndStartsEmpty()
    {
        var runs = NewRuns();
        await runs.CommitCohortAsync(MakeCohort("Opt"), Ct);
        var first = runs.Path;

        runs.StartNew();

        Assert.Equal("2026-10-02 14-30 (2)", runs.Name);
        Assert.NotEqual(first, runs.Path);
        Assert.Empty(await runs.ListAsync(Ct));
        Assert.True(File.Exists(first), "Starting a new run must leave the earlier one on disk.");
    }

    [Fact]
    public void StartNew_BeforeAnyCommit_StillGetsADistinctName()
    {
        // Nothing is on disk yet, so only the current path can collide.
        var runs = NewRuns();

        runs.StartNew();

        Assert.Equal("2026-10-02 14-30 (2)", runs.Name);
    }

    [Fact]
    public async Task OpenAsync_AnEarlierRun_ShowsItsRows_AndLaterCommitsMergeIntoIt()
    {
        var runs = NewRuns();
        await runs.CommitCohortAsync(MakeCohort("Opt", "Shock"), Ct);
        var earlier = runs.Path;
        runs.StartNew();

        await runs.OpenAsync(earlier, Ct);
        Assert.Equal(earlier, runs.Path);
        Assert.Equal(2, (await runs.ListAsync(Ct)).Count);

        await runs.CommitCohortAsync(MakeCohort("Opt"), Ct);

        var rows = await runs.ListAsync(Ct);
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows.Single(r => r.OracleName == "Opt").Quantity);
    }

    [Fact]
    public async Task OpenAsync_AMoxfieldExport_Throws_AndKeepsTheCurrentRun()
    {
        var runs = NewRuns();
        await runs.CommitCohortAsync(MakeCohort("Opt"), Ct);
        var current = runs.Path;

        var export = Path.Combine(_root, "moxfield.csv");
        await File.WriteAllTextAsync(export,
            "Count,Name,Edition,Condition,Language,Foil,Collector Number,Alter,Playtest Card,Purchase Price\n1,Opt,,,,,,,,\n",
            Ct);

        await Assert.ThrowsAnyAsync<FormatException>(() => runs.OpenAsync(export, Ct));

        Assert.Equal(current, runs.Path);
        Assert.Single(await runs.ListAsync(Ct));
    }

    [Fact]
    public async Task OpenAsync_AMissingFile_Throws_AndKeepsTheCurrentRun()
    {
        var runs = NewRuns();
        var current = runs.Path;

        await Assert.ThrowsAsync<FileNotFoundException>(() => runs.OpenAsync(Path.Combine(_root, "gone.csv"), Ct));

        Assert.Equal(current, runs.Path);
    }

    [Fact]
    public async Task Rename_MovesTheFileAndItsBackup_AndCommitsFollowTheNewName()
    {
        var runs = NewRuns();
        await runs.CommitCohortAsync(MakeCohort("Opt"), Ct);
        await runs.CommitCohortAsync(MakeCohort("Shock"), Ct); // second write leaves a .bak
        var oldPath = runs.Path;
        Assert.True(File.Exists(oldPath + ".bak"));

        runs.Rename("Binder 3");

        Assert.Equal("Binder 3", runs.Name);
        Assert.Equal(Path.Combine(RunsFolder, "Binder 3.csv"), runs.Path);
        Assert.False(File.Exists(oldPath));
        Assert.False(File.Exists(oldPath + ".bak"));
        Assert.True(File.Exists(runs.Path + ".bak"));

        await runs.CommitCohortAsync(MakeCohort("Bolt"), Ct);
        Assert.Equal(3, (await runs.ListAsync(Ct)).Count);
    }

    [Fact]
    public void Rename_BeforeTheFirstCommit_OnlyChangesTheName()
    {
        var runs = NewRuns();

        runs.Rename("Trade box.csv");

        Assert.Equal("Trade box", runs.Name);
        Assert.False(File.Exists(runs.Path));
    }

    [Fact]
    public async Task Rename_OntoAnExistingRun_Throws_AndChangesNothing()
    {
        var runs = NewRuns();
        await runs.CommitCohortAsync(MakeCohort("Opt"), Ct);
        var other = runs.Path;
        runs.StartNew();
        await runs.CommitCohortAsync(MakeCohort("Shock"), Ct);
        var current = runs.Path;

        Assert.Throws<IOException>(() => runs.Rename(Path.GetFileNameWithoutExtension(other)));

        Assert.Equal(current, runs.Path);
        Assert.True(File.Exists(current));
        Assert.Single(await runs.ListAsync(Ct));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b")]
    public void Rename_ToAnUnusableName_Throws(string name)
    {
        var runs = NewRuns();
        var current = runs.Path;

        Assert.Throws<ArgumentException>(() => runs.Rename(name));

        Assert.Equal(current, runs.Path);
    }

    [Fact]
    public async Task ListRecent_IsNewestFirst_AndLeavesOutTheCurrentRunAndBackups()
    {
        var runs = NewRuns();
        Assert.Empty(runs.ListRecent());

        await runs.CommitCohortAsync(MakeCohort("Opt"), Ct);
        await runs.CommitCohortAsync(MakeCohort("Opt"), Ct); // .bak beside it
        var older = runs.Path;
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-2));

        runs.StartNew();
        await runs.CommitCohortAsync(MakeCohort("Shock"), Ct);
        var newer = runs.Path;
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddHours(-1));

        // The current run is on disk and the newest file, yet not offered.
        Assert.Equal(new[] { older }, runs.ListRecent());

        runs.StartNew();

        Assert.Equal(new[] { newer, older }, runs.ListRecent());
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
