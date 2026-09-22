using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LoreFetch.Core.Abstractions;

namespace LoreFetch.App.ViewModels;

/// <summary>
/// View model for the main window — the app's first view model (A4).
/// Exposes the expected-count selector (1 / 3 / 9) and the auto-capture
/// toggle, each writing through to the shared <see cref="ScanSettings"/>
/// instance immediately on set.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ScanSettings"/> has no <c>INotifyPropertyChanged</c>, so
/// binding the UI directly to it would render the initial state and then
/// never update. This view model wraps it: it holds its own backing field
/// for each property, raises INPC via <see cref="ObservableObject"/>, and
/// writes the new value to <see cref="ScanSettings"/> in the same setter.
/// </para>
/// <para>
/// The cross-thread write is benign — <c>int</c> and <c>bool</c> are
/// atomically readable and writable on all CLR-supported architectures,
/// and the pipeline observes the new value on the next processed frame.
/// </para>
/// </remarks>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly ScanSettings _settings;

    public MainViewModel(ScanSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;

        // Mirror whatever defaults ScanSettings came with so the initial
        // display is consistent with the pipeline's own state.
        _expectedCount = settings.ExpectedCount;
        _autoCaptureEnabled = settings.AutoCaptureEnabled;
    }

    // ------------------------------------------------------------------
    // ExpectedCount — 1 / 3 / 9
    // ------------------------------------------------------------------

    private int _expectedCount;

    /// <summary>
    /// Expected card count for the next capture: 1, 3 or 9.
    /// Setting any other value is a silent no-op — the UI only offers the
    /// three valid choices.
    /// </summary>
    public int ExpectedCount
    {
        get => _expectedCount;
        set
        {
            if (value is not (1 or 3 or 9)) return;
            if (!SetProperty(ref _expectedCount, value)) return;

            _settings.ExpectedCount = value;

            // Notify the three radio-button helpers so their bindings
            // update when ExpectedCount changes from code (e.g. a test
            // setting the property directly rather than through a control).
            OnPropertyChanged(nameof(IsCount1));
            OnPropertyChanged(nameof(IsCount3));
            OnPropertyChanged(nameof(IsCount9));
        }
    }

    /// <summary>True when <see cref="ExpectedCount"/> equals 1.</summary>
    /// Setting to <c>true</c> sets <see cref="ExpectedCount"/> = 1.
    /// Setting to <c>false</c> is a no-op — only a competing radio button
    /// going true should change the count, and Avalonia fires that setter.
    public bool IsCount1
    {
        get => ExpectedCount == 1;
        set { if (value) ExpectedCount = 1; }
    }

    /// <summary>True when <see cref="ExpectedCount"/> equals 3.</summary>
    public bool IsCount3
    {
        get => ExpectedCount == 3;
        set { if (value) ExpectedCount = 3; }
    }

    /// <summary>True when <see cref="ExpectedCount"/> equals 9.</summary>
    public bool IsCount9
    {
        get => ExpectedCount == 9;
        set { if (value) ExpectedCount = 9; }
    }

    // ------------------------------------------------------------------
    // AutoCaptureEnabled
    // ------------------------------------------------------------------

    private bool _autoCaptureEnabled;

    /// <summary>
    /// Whether auto-capture fires when the count-gated settle condition is
    /// met. Writes through to <see cref="ScanSettings"/> immediately on set.
    /// </summary>
    public bool AutoCaptureEnabled
    {
        get => _autoCaptureEnabled;
        set
        {
            if (!SetProperty(ref _autoCaptureEnabled, value)) return;
            _settings.AutoCaptureEnabled = value;
        }
    }

    // ------------------------------------------------------------------
    // Cohort tiles — the grid of TileViewModel wrappers (A5)
    // ------------------------------------------------------------------

    private readonly ObservableCollection<TileViewModel> _tiles = new();

    /// <summary>
    /// The tile view-models for the current cohort, bound to the cohort grid
    /// in <c>MainWindow</c>. Empty until <see cref="LoadCohort"/> is called
    /// and cleared when a new cohort replaces the old one.
    /// </summary>
    public ObservableCollection<TileViewModel> Tiles => _tiles;

    /// <summary>
    /// Replaces the tile collection with view-models wrapping
    /// <paramref name="cohort"/>'s tiles, in order. A7 (Space / auto-capture)
    /// calls this when a new cohort arrives; A5 provides it so the grid and
    /// its tests can bind without wiring capture logic.
    /// </summary>
    public void LoadCohort(Cohort cohort)
    {
        ArgumentNullException.ThrowIfNull(cohort);
        _tiles.Clear();
        foreach (var tile in cohort.Tiles)
        {
            _tiles.Add(new TileViewModel(tile));
        }
    }
}
