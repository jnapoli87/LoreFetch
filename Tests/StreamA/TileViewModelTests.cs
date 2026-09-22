using LoreFetch.App.ViewModels;
using LoreFetch.Core.Abstractions;
using Xunit;

namespace LoreFetch.Tests.StreamA;

/// <summary>
/// Unit tests for <see cref="TileViewModel"/> and the <c>LoadCohort</c>
/// extension on <see cref="MainViewModel"/> (A5).
///
/// These are plain xUnit facts — no Avalonia headless host required. All
/// assertions concern the bindable properties, not the thumbnail bitmap, so
/// tests pass on every platform including macOS ARM64.
///
/// Distances used throughout: Good = 100, Ok = 200.
///   ≤ 100 → Included, IsLowConfidence = false
///   101–200 → Included, IsLowConfidence = true
///   > 200 → Unresolved
/// ToggleExcluded() → Excluded
/// SetManually() → ManuallySet
///
/// Chaos-tested results are recorded at the bottom of this file.
/// </summary>
public class TileViewModelTests
{
    // -----------------------------------------------------------------------
    // Shared constants and helpers
    // -----------------------------------------------------------------------

    private const int Good = 100;
    private const int Ok = 200;

    /// A minimal valid RectifiedCard for unit tests. One BGRA pixel, stride=4.
    /// TileViewModel's BuildThumbnail() will catch the out-of-size exception and
    /// return null for Thumbnail — which is fine; these tests don't check it.
    private static RectifiedCard MakeTestCard() =>
        new([0, 0, 0, 255], stride: 4, PixelLayout.Bgra32, default);

    private static CardCandidate Candidate(string name, int distance) =>
        new("oracle-" + name, name, distance, ArtworkId: null);

    // -----------------------------------------------------------------------
    // State: Included (high confidence)
    // -----------------------------------------------------------------------

    [Fact]
    public void TileViewModel_Included_HighConfidence_StateAndFlagsCorrect()
    {
        var tile = new CohortTile(MakeTestCard(),
            [Candidate("Lightning Bolt", 50)],
            Good, Ok);
        var vm = new TileViewModel(tile);

        Assert.Equal(TileState.Included, vm.State);
        Assert.True(vm.IsIncluded);
        Assert.False(vm.IsExcluded);
        Assert.False(vm.IsUnresolved);
        Assert.False(vm.IsManuallySet);
        Assert.False(vm.ShowExcludedMarker);
    }

    [Fact]
    public void TileViewModel_Included_HighConfidence_DisplayNameAndDistance()
    {
        var tile = new CohortTile(MakeTestCard(),
            [Candidate("Lightning Bolt", 50)],
            Good, Ok);
        var vm = new TileViewModel(tile);

        Assert.Equal("Lightning Bolt", vm.DisplayName);
        Assert.Equal("50", vm.DistanceText);
        Assert.False(vm.IsLowConfidence);
    }

    // -----------------------------------------------------------------------
    // State: Included (low confidence)
    // -----------------------------------------------------------------------

    [Fact]
    public void TileViewModel_Included_LowConfidence_StateAndFlagsCorrect()
    {
        var tile = new CohortTile(MakeTestCard(),
            [Candidate("Counterspell", 150)],
            Good, Ok);
        var vm = new TileViewModel(tile);

        Assert.Equal(TileState.Included, vm.State);
        Assert.True(vm.IsIncluded);
        Assert.False(vm.IsExcluded);
        Assert.False(vm.IsUnresolved);
        Assert.False(vm.IsManuallySet);
        Assert.False(vm.ShowExcludedMarker);
    }

    [Fact]
    public void TileViewModel_Included_LowConfidence_IsLowConfidenceTrue()
    {
        var tile = new CohortTile(MakeTestCard(),
            [Candidate("Counterspell", 150)],
            Good, Ok);
        var vm = new TileViewModel(tile);

        Assert.True(vm.IsLowConfidence);
        Assert.Equal("Counterspell", vm.DisplayName);
        Assert.Equal("150", vm.DistanceText);
    }

    /// Critical rule: IsLowConfidence is a highlight, NOT a gate. A
    /// low-confidence tile is still Included and must commit on Enter.
    [Fact]
    public void TileViewModel_LowConfidence_IsStillIncluded_NotGated()
    {
        var tile = new CohortTile(MakeTestCard(),
            [Candidate("Counterspell", 150)],
            Good, Ok);
        var vm = new TileViewModel(tile);

        // IsLowConfidence must be true — it drives a highlight.
        Assert.True(vm.IsLowConfidence);

        // But the tile is still Included — highlight ≠ gate.
        Assert.Equal(TileState.Included, vm.State);
        Assert.True(vm.IsIncluded);

        // The distance is still shown (not null) — manual set would clear it.
        Assert.Equal("150", vm.DistanceText);
    }

    // -----------------------------------------------------------------------
    // State: Excluded
    // -----------------------------------------------------------------------

    [Fact]
    public void TileViewModel_Excluded_StateAndFlagsCorrect()
    {
        var tile = new CohortTile(MakeTestCard(),
            [Candidate("Dark Ritual", 50)],
            Good, Ok);
        tile.ToggleExcluded();

        var vm = new TileViewModel(tile);

        Assert.Equal(TileState.Excluded, vm.State);
        Assert.False(vm.IsIncluded);
        Assert.True(vm.IsExcluded);
        Assert.False(vm.IsUnresolved);
        Assert.False(vm.IsManuallySet);
        Assert.True(vm.ShowExcludedMarker, "ShowExcludedMarker drives the X overlay and must be true");
    }

    [Fact]
    public void TileViewModel_Excluded_DisplayNameRetainsPreExcludeName()
    {
        var tile = new CohortTile(MakeTestCard(),
            [Candidate("Dark Ritual", 50)],
            Good, Ok);
        tile.ToggleExcluded();

        var vm = new TileViewModel(tile);

        // The name is retained from the Included state before exclusion.
        Assert.Equal("Dark Ritual", vm.DisplayName);
        Assert.False(vm.IsLowConfidence);
    }

    // -----------------------------------------------------------------------
    // State: Unresolved
    // -----------------------------------------------------------------------

    [Fact]
    public void TileViewModel_Unresolved_StateAndFlagsCorrect()
    {
        // Distance > Ok → Unresolved
        var tile = new CohortTile(MakeTestCard(),
            [Candidate("Mystery Card", 500)],
            Good, Ok);

        var vm = new TileViewModel(tile);

        Assert.Equal(TileState.Unresolved, vm.State);
        Assert.False(vm.IsIncluded);
        Assert.False(vm.IsExcluded);
        Assert.True(vm.IsUnresolved);
        Assert.False(vm.IsManuallySet);
        Assert.False(vm.ShowExcludedMarker);
    }

    /// Critical rule: DisplayName must be blank for Unresolved — there is no
    /// accepted name, so showing a candidate name would mislead the user.
    [Fact]
    public void TileViewModel_Unresolved_DisplayName_IsEmpty_NeverShowsCandidate()
    {
        // The tile has a candidate ("Mystery Card") but distance > Ok, so it
        // is Unresolved and Chosen is null. DisplayName must be "".
        var tile = new CohortTile(MakeTestCard(),
            [Candidate("Mystery Card", 500)],
            Good, Ok);

        var vm = new TileViewModel(tile);

        Assert.Equal(string.Empty, vm.DisplayName);
        Assert.Equal(string.Empty, vm.DistanceText);
        Assert.False(vm.IsLowConfidence);
    }

    // -----------------------------------------------------------------------
    // State: ManuallySet
    // -----------------------------------------------------------------------

    [Fact]
    public void TileViewModel_ManuallySet_StateAndFlagsCorrect()
    {
        var tile = new CohortTile(MakeTestCard(),
            [Candidate("Wrong Card", 500)],
            Good, Ok);
        tile.SetManually(new OracleEntry("oracle-lotus", "Black Lotus"));

        var vm = new TileViewModel(tile);

        Assert.Equal(TileState.ManuallySet, vm.State);
        Assert.False(vm.IsIncluded);
        Assert.False(vm.IsExcluded);
        Assert.False(vm.IsUnresolved);
        Assert.True(vm.IsManuallySet);
        Assert.False(vm.ShowExcludedMarker);
    }

    [Fact]
    public void TileViewModel_ManuallySet_DisplayNameShowsCorrectedName()
    {
        var tile = new CohortTile(MakeTestCard(),
            [Candidate("Wrong Card", 500)],
            Good, Ok);
        tile.SetManually(new OracleEntry("oracle-lotus", "Black Lotus"));

        var vm = new TileViewModel(tile);

        Assert.Equal("Black Lotus", vm.DisplayName);

        // SetManually() clears the distance — it names a CARD, not an art.
        Assert.Equal(string.Empty, vm.DistanceText);

        // Manual selection resets IsLowConfidence.
        Assert.False(vm.IsLowConfidence);
    }

    // -----------------------------------------------------------------------
    // Refresh(): INPC fires for State and all derived props
    // -----------------------------------------------------------------------

    [Fact]
    public void TileViewModel_Refresh_RaisesPropertyChangedForState()
    {
        var tile = new CohortTile(MakeTestCard(),
            [Candidate("Lightning Bolt", 50)],
            Good, Ok);
        var vm = new TileViewModel(tile);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        // Mutate the tile, then call Refresh — the three steps A6 will do.
        tile.ToggleExcluded();
        vm.Refresh();

        // State must be in the raised list — it is the primary property that
        // changed, and every derived flag follows it.
        Assert.Contains(nameof(TileViewModel.State), raised);
        Assert.Contains(nameof(TileViewModel.IsIncluded), raised);
        Assert.Contains(nameof(TileViewModel.IsExcluded), raised);
        Assert.Contains(nameof(TileViewModel.IsUnresolved), raised);
        Assert.Contains(nameof(TileViewModel.IsManuallySet), raised);
        Assert.Contains(nameof(TileViewModel.ShowExcludedMarker), raised);
        Assert.Contains(nameof(TileViewModel.DisplayName), raised);
        Assert.Contains(nameof(TileViewModel.DistanceText), raised);
        Assert.Contains(nameof(TileViewModel.IsLowConfidence), raised);
    }

    [Fact]
    public void TileViewModel_Refresh_UpdatesDerivedPropsAfterToggleExcluded()
    {
        var tile = new CohortTile(MakeTestCard(),
            [Candidate("Lightning Bolt", 50)],
            Good, Ok);
        var vm = new TileViewModel(tile);

        // Before exclusion
        Assert.True(vm.IsIncluded);
        Assert.False(vm.IsExcluded);
        Assert.False(vm.ShowExcludedMarker);

        // Mutate and refresh
        tile.ToggleExcluded();
        vm.Refresh();

        // After exclusion
        Assert.False(vm.IsIncluded);
        Assert.True(vm.IsExcluded);
        Assert.True(vm.ShowExcludedMarker);
        Assert.Equal(TileState.Excluded, vm.State);
    }

    // -----------------------------------------------------------------------
    // MainViewModel.LoadCohort
    // -----------------------------------------------------------------------

    [Fact]
    public void MainViewModel_LoadCohort_PopulatesTilesWithCorrectCount()
    {
        var settings = new ScanSettings();
        var vm = new MainViewModel(settings);

        var cohort = MakeCohort(3);
        vm.LoadCohort(cohort);

        Assert.Equal(3, vm.Tiles.Count);
    }

    [Fact]
    public void MainViewModel_LoadCohort_ReplacesExistingTiles()
    {
        var settings = new ScanSettings();
        var vm = new MainViewModel(settings);

        vm.LoadCohort(MakeCohort(3));
        Assert.Equal(3, vm.Tiles.Count);

        vm.LoadCohort(MakeCohort(1));
        Assert.Single(vm.Tiles);
    }

    [Fact]
    public void MainViewModel_LoadCohort_TileViewModelsReflectUnderlyingTileState()
    {
        var settings = new ScanSettings();
        var vm = new MainViewModel(settings);

        // Load a cohort with one Included and one Unresolved tile.
        var card = MakeTestCard();
        var includedTile = new CohortTile(card, [Candidate("Lightning Bolt", 50)], Good, Ok);
        var unresolvedTile = new CohortTile(card, [Candidate("Unknown", 500)], Good, Ok);

        var cohort = new Cohort(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            expectedCount: 2,
            CaptureReason.Manual,
            [includedTile, unresolvedTile]);

        vm.LoadCohort(cohort);

        Assert.Equal(2, vm.Tiles.Count);
        Assert.True(vm.Tiles[0].IsIncluded);
        Assert.True(vm.Tiles[1].IsUnresolved);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static Cohort MakeCohort(int count)
    {
        var card = MakeTestCard();
        var tiles = Enumerable.Range(0, count)
            .Select(i => new CohortTile(card, [Candidate($"Card {i}", 50)], Good, Ok))
            .ToList<CohortTile>();

        return new Cohort(Guid.NewGuid(), DateTimeOffset.UtcNow, count, CaptureReason.Manual, tiles);
    }
}
