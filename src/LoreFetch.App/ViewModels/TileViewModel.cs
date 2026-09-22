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
/// mutators and is the only consumer of <see cref="ToggleExcludedFromUi"/>,
/// <see cref="SetManuallyFromUi"/> and <see cref="ClearFromUi"/>. Each of
/// those methods calls the tile's own mutator and then <see cref="Refresh"/>
/// so INPC fires — the UI never assigns <c>State</c> or <c>Chosen</c>
/// directly.
/// </para>
/// </remarks>
public sealed class TileViewModel : ObservableObject
{
    private readonly CohortTile _tile;
    private readonly IOracleCatalog? _catalog;

    /// <param name="tile">The underlying domain tile. Must not be null.</param>
    /// <param name="catalog">
    /// The oracle catalog for the "Set card manually…" type-ahead. When
    /// <c>null</c> the populator returns only the tile's own runner-up
    /// candidates (or nothing) — it never throws.
    /// </param>
    public TileViewModel(CohortTile tile, IOracleCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(tile);
        _tile = tile;
        _catalog = catalog;
        Tile = tile;
        Thumbnail = BuildThumbnail(tile.Image);
        TypeAheadPopulator = BuildPopulator(tile, catalog);
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
    // A6: Mouse-interaction methods — each = tile mutator + Refresh()
    // The UI NEVER assigns State or Chosen directly (reviewer point 5).
    // ------------------------------------------------------------------

    /// <summary>
    /// Left-click handler: toggles the X exclusion marker on and off.
    /// The opt-out default is <see cref="TileState.Included"/> — this method
    /// never flips that default (reviewer point 6).
    /// </summary>
    public void ToggleExcludedFromUi()
    {
        _tile.ToggleExcluded();
        Refresh();
    }

    /// <summary>
    /// "Set card manually…" selection handler: applies the user's chosen
    /// entry and updates state to <see cref="TileState.ManuallySet"/>.
    /// </summary>
    public void SetManuallyFromUi(OracleEntry entry)
    {
        _tile.SetManually(entry);
        Refresh();
    }

    /// <summary>
    /// "Clear" menu-item handler: reverts a manual choice back to the
    /// hash's own proposal (<see cref="TileState.Included"/> or
    /// <see cref="TileState.Unresolved"/>). Does NOT remove the tile —
    /// that is the X's job.
    /// </summary>
    public void ClearFromUi()
    {
        _tile.Clear();
        Refresh();
    }

    // ------------------------------------------------------------------
    // A6: Type-ahead for "Set card manually…"
    // ------------------------------------------------------------------

    private bool _isTypeAheadOpen;

    /// <summary>
    /// Whether the inline type-ahead AutoCompleteBox is visible. Set to
    /// <c>true</c> when the user clicks "Set card manually…"; set back to
    /// <c>false</c> after a selection is made.
    /// </summary>
    public bool IsTypeAheadOpen
    {
        get => _isTypeAheadOpen;
        set => SetProperty(ref _isTypeAheadOpen, value);
    }

    /// <summary>
    /// The <c>AsyncPopulator</c> delegate for the "Set card manually…"
    /// <c>AutoCompleteBox</c>. Assigned once in the constructor via
    /// <see cref="BuildPopulator"/>; the code-behind reads it via
    /// <c>OnTypeAheadLoaded</c> and sets it on the control directly.
    /// </summary>
    /// <remarks>
    /// The populator:
    /// <list type="bullet">
    ///   <item>Yields to a background thread before scanning.</item>
    ///   <item>Honours the <see cref="CancellationToken"/> — keystroke N cancels N−1.</item>
    ///   <item>Returns <see cref="CohortTile.Candidates"/> runners-up first, then
    ///   <see cref="IOracleCatalog.All"/> matches, deduped by OracleId.</item>
    ///   <item>Filters by <c>StartsWithOrdinal</c> (no culture work).</item>
    ///   <item>Caps results at 20 regardless of catalog size.</item>
    ///   <item>Returns an empty sequence — never throws — when the catalog is null.</item>
    /// </list>
    /// </remarks>
    public Func<string?, CancellationToken, Task<IEnumerable<object>>> TypeAheadPopulator { get; }

    private static Func<string?, CancellationToken, Task<IEnumerable<object>>> BuildPopulator(
        CohortTile tile, IOracleCatalog? catalog)
    {
        return async (prefix, ct) =>
        {
            if (string.IsNullOrEmpty(prefix) || prefix.Length < 2)
            {
                return Array.Empty<object>();
            }

            // Filter off the UI thread so the 33k scan never blocks renders.
            await Task.Yield();
            ct.ThrowIfCancellationRequested();

            var results = new List<CatalogItem>(20);
            var seen = new HashSet<string>(StringComparer.Ordinal); // dedupe by OracleId

            // --- Runners-up first: the identifier's ranked candidate list ---
            foreach (var c in tile.Candidates)
            {
                ct.ThrowIfCancellationRequested();
                if (c.OracleName.StartsWith(prefix, StringComparison.Ordinal) && seen.Add(c.OracleId))
                {
                    results.Add(new CatalogItem(c.OracleId, c.OracleName));
                    if (results.Count >= 20) return results;
                }
            }

            // --- Then catalog matches, deduped against the runners-up already in results ---
            if (catalog != null)
            {
                foreach (var e in catalog.All)
                {
                    ct.ThrowIfCancellationRequested();
                    if (e.OracleName.StartsWith(prefix, StringComparison.Ordinal) && seen.Add(e.OracleId))
                    {
                        results.Add(new CatalogItem(e.OracleId, e.OracleName));
                        if (results.Count >= 20) return results;
                    }
                }
            }

            return results;
        };
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
