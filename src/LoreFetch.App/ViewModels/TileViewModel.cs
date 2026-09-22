using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using LoreFetch.Core.Abstractions;

namespace LoreFetch.App.ViewModels;

/// <summary>
/// View model wrapping one <see cref="CohortTile"/> for the cohort grid (A5).
/// Exposes bindable properties derived from the tile's current state. The VM
/// reflects what the tile already computed — it NEVER compares
/// <see cref="CohortTile.ChosenDistance"/> to a threshold itself; that
/// comparison lives entirely inside <see cref="CohortTile"/>, which sets
/// <see cref="CohortTile.IsLowConfidence"/> accordingly.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CohortTile"/> has no <c>INotifyPropertyChanged</c>. The UI is
/// the only mutator after construction (the pipeline constructs tiles and never
/// touches them again), so this wrapper cannot miss a change it didn't cause.
/// After calling any tile mutator (<c>ToggleExcluded</c>, <c>SetManually</c>,
/// <c>Clear</c>), the caller must invoke <see cref="Refresh"/> so that INPC
/// fires and bindings re-read the updated values.
/// </para>
/// <para>
/// A6 provides the click handlers and context menus that call the tile's
/// mutators. A5 exposes only <see cref="Tile"/> (so A6 can reach the mutators)
/// and <see cref="Refresh"/> (the hook A6 calls after each mutation). No
/// interaction code belongs here.
/// </para>
/// </remarks>
public sealed class TileViewModel : ObservableObject
{
    private readonly CohortTile _tile;

    public TileViewModel(CohortTile tile)
    {
        ArgumentNullException.ThrowIfNull(tile);
        _tile = tile;
        Tile = tile;
        Thumbnail = BuildThumbnail(tile.Image);
    }

    // ------------------------------------------------------------------
    // Access to the underlying tile — A6 calls the tile's mutators directly
    // so it can reach ToggleExcluded / SetManually / Clear without this VM
    // wrapping them (which is explicitly out of scope for A5).
    // ------------------------------------------------------------------

    /// <summary>The underlying tile. A6 calls its mutators directly.</summary>
    public CohortTile Tile { get; }

    // ------------------------------------------------------------------
    // State — four tile states, each exposed as a bool flag for XAML binding
    // ------------------------------------------------------------------

    /// <summary>The current state of the underlying tile.</summary>
    public TileState State => _tile.State;

    /// <summary>True when the tile is in the <see cref="TileState.Included"/> state.</summary>
    public bool IsIncluded => _tile.State == TileState.Included;

    /// <summary>True when the tile is in the <see cref="TileState.Excluded"/> state.</summary>
    public bool IsExcluded => _tile.State == TileState.Excluded;

    /// <summary>True when the tile is in the <see cref="TileState.Unresolved"/> state.</summary>
    public bool IsUnresolved => _tile.State == TileState.Unresolved;

    /// <summary>True when the tile is in the <see cref="TileState.ManuallySet"/> state.</summary>
    public bool IsManuallySet => _tile.State == TileState.ManuallySet;

    /// <summary>
    /// True when the X exclusion marker should be shown. Equivalent to
    /// <see cref="IsExcluded"/> but named for display intent — XAML reads it
    /// as a statement about what to render, not about what state the tile is in.
    /// </summary>
    public bool ShowExcludedMarker => _tile.State == TileState.Excluded;

    // ------------------------------------------------------------------
    // Display text
    // ------------------------------------------------------------------

    /// <summary>
    /// The oracle name to display. <b>Blank when <see cref="TileState.Unresolved"/></b>
    /// — the hash found no match within the ok-distance threshold, so there is
    /// no proposed name to show. For <see cref="TileState.Included"/> and
    /// <see cref="TileState.ManuallySet"/> the tile always has a non-null
    /// <see cref="CohortTile.Chosen"/>; for <see cref="TileState.Excluded"/> it
    /// carries whichever name it had before exclusion.
    /// </summary>
    public string DisplayName => _tile.State == TileState.Unresolved
        ? string.Empty
        : _tile.Chosen?.OracleName ?? string.Empty;

    /// <summary>
    /// The Hamming distance as a display string, or an empty string when the
    /// distance is not available. Distance is null when the tile is
    /// <see cref="TileState.ManuallySet"/> (a manual pick names a card, not
    /// an art) or <see cref="TileState.Unresolved"/> (no match at all).
    /// </summary>
    public string DistanceText => _tile.ChosenDistance.HasValue
        ? _tile.ChosenDistance.Value.ToString()
        : string.Empty;

    // ------------------------------------------------------------------
    // Low-confidence — highlight only, NEVER a gate
    // ------------------------------------------------------------------

    /// <summary>
    /// True when the tile's best match fell in the ok-but-not-good range.
    /// This drives a visual highlight only — a low-confidence tile is still
    /// <see cref="TileState.Included"/> and still commits on Enter.
    /// </summary>
    /// <remarks>
    /// The VM reflects this directly from <see cref="CohortTile.IsLowConfidence"/>
    /// and NEVER re-derives it by comparing <see cref="CohortTile.ChosenDistance"/>
    /// to a threshold. That comparison happens once, inside
    /// <see cref="CohortTile"/>, which is the only place in the codebase that
    /// applies thresholds.
    /// </remarks>
    public bool IsLowConfidence => _tile.IsLowConfidence;

    // ------------------------------------------------------------------
    // Thumbnail
    // ------------------------------------------------------------------

    /// <summary>
    /// The tile's card image as a <see cref="WriteableBitmap"/>, or
    /// <c>null</c> when the Avalonia platform is not initialised (e.g. in
    /// plain xUnit tests that do not use <c>AvaloniaFact</c>) or when the
    /// card buffer is too small for the canonical 488×680 dimensions.
    /// Built once in the constructor via <see cref="PixelConvert.ToBgra32"/>.
    /// </summary>
    public WriteableBitmap? Thumbnail { get; }

    // ------------------------------------------------------------------
    // Refresh — called by A6 after mutating the underlying tile
    // ------------------------------------------------------------------

    /// <summary>
    /// Raises <c>PropertyChanged</c> for every derived property after the
    /// underlying <see cref="CohortTile"/> has been mutated (by A6 — it calls
    /// the tile's mutator first, then this method). Does NOT call any tile
    /// mutator — that is A6's job.
    /// </summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsIncluded));
        OnPropertyChanged(nameof(IsExcluded));
        OnPropertyChanged(nameof(IsUnresolved));
        OnPropertyChanged(nameof(IsManuallySet));
        OnPropertyChanged(nameof(ShowExcludedMarker));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(DistanceText));
        OnPropertyChanged(nameof(IsLowConfidence));
    }

    // ------------------------------------------------------------------
    // Thumbnail construction
    // ------------------------------------------------------------------

    /// Converts the <see cref="RectifiedCard"/>'s pixels to a
    /// <see cref="WriteableBitmap"/> using the same path as the live preview
    /// (<see cref="PixelConvert.ToBgra32"/> + <see cref="Marshal.Copy"/>).
    /// Returns <c>null</c> on any exception — the Avalonia platform may not
    /// be initialised in plain unit tests, and a test card buffer may be
    /// intentionally smaller than the canonical 488×680 dimensions.
    private static WriteableBitmap? BuildThumbnail(RectifiedCard card)
    {
        try
        {
            var width = RectifiedCard.CanonicalWidth;
            var height = RectifiedCard.CanonicalHeight;

            var bitmap = new WriteableBitmap(
                new PixelSize(width, height),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Opaque);

            using var fb = bitmap.Lock();
            var rowBytes = fb.RowBytes;
            var converted = new byte[rowBytes * height];

            PixelConvert.ToBgra32(
                card.Pixels.Span,
                width, height,
                card.Stride, card.Layout,
                converted, rowBytes);

            Marshal.Copy(converted, 0, fb.Address, rowBytes * height);
            return bitmap;
        }
        catch
        {
            // Avalonia platform not registered (plain unit tests), or the card
            // buffer is too small for the canonical size — return null so the
            // Image binding shows a transparent placeholder rather than crashing.
            return null;
        }
    }
}
