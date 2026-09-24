using LoreFetch.Core.Abstractions;

namespace LoreFetch.App.ViewModels;

/// <summary>
/// Wraps one <see cref="CollectionRow"/> for the collection <c>DataGrid</c>
/// (A10-fix bug 3). <see cref="CollectionRow"/> is a frozen record struct in
/// <c>Core/Abstractions</c> and stores <see cref="CollectionRow.LastScannedAt"/>
/// in UTC (DECISIONS.md's storage contract — the file itself never changes);
/// this wrapper adds a display-only local-time projection so the grid can
/// show something unambiguous without touching the stored value at all.
/// </summary>
/// <remarks>
/// <see cref="LastScannedLocalText"/> takes the display time zone as a
/// constructor parameter (defaulting to <see cref="TimeZoneInfo.Local"/>)
/// specifically so a unit test can pin a fixed zone rather than depend on
/// the machine running the test — DECISIONS.md's chaos-testing standard needs a
/// deterministic assertion, and "whatever this machine's local zone happens
/// to be" is not one.
/// </remarks>
public sealed class CollectionRowItem
{
    /// <summary>The unambiguous, sortable local-time format: 24-hour, no locale variance.</summary>
    public const string LocalTimeFormat = "yyyy-MM-dd HH:mm:ss";

    public CollectionRowItem(CollectionRow row, TimeZoneInfo? displayTimeZone = null)
    {
        Row = row;
        var zone = displayTimeZone ?? TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTime(row.LastScannedAt, zone);
        LastScannedLocalText = local.ToString(LocalTimeFormat);
    }

    /// <summary>The underlying row — untouched, still UTC.</summary>
    public CollectionRow Row { get; }

    public string OracleId => Row.OracleId;
    public string OracleName => Row.OracleName;
    public int Quantity => Row.Quantity;
    public string? Condition => Row.Condition;
    public int? BestMatchDistance => Row.BestMatchDistance;
    public RowSource Source => Row.Source;
    public string? ArtworkId => Row.ArtworkId;

    /// <summary>
    /// <see cref="Row"/>'s <see cref="CollectionRow.LastScannedAt"/> converted
    /// to the display time zone and formatted as <see cref="LocalTimeFormat"/>
    /// (24-hour, e.g. "2026-09-22 14:05:30"). The stored value stays UTC —
    /// this is a display-only projection, never written back anywhere.
    /// </summary>
    public string LastScannedLocalText { get; }
}
