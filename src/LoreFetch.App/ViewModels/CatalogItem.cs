using LoreFetch.Core.Abstractions;

namespace LoreFetch.App.ViewModels;

/// <summary>
/// Wrapper around <see cref="OracleEntry"/> that gives <c>AutoCompleteBox</c>
/// a display-friendly <see cref="ToString"/> — returns the oracle name so the
/// type-ahead shows card names rather than the record's structural repr.
/// </summary>
/// <remarks>
/// <see cref="OracleEntry"/> is a frozen record struct in
/// <c>Core/Abstractions</c>; its <c>ToString()</c> cannot be overridden there.
/// This wrapper lives in the App layer, outside the frozen surface.
/// </remarks>
internal sealed class CatalogItem
{
    public CatalogItem(string oracleId, string oracleName)
    {
        OracleId = oracleId;
        OracleName = oracleName;
    }

    public string OracleId { get; }
    public string OracleName { get; }

    /// Converts back to the domain type that <see cref="LoreFetch.Core.Abstractions.CohortTile.SetManually"/> accepts.
    public OracleEntry ToEntry() => new(OracleId, OracleName);

    /// Returns <see cref="OracleName"/> — the text <c>AutoCompleteBox</c>
    /// places in its text input after a selection.
    public override string ToString() => OracleName;
}
