using LoreFetch.Core.Abstractions;

namespace LoreFetch.App.ViewModels;

/// <summary>
/// View-model wrapper for one <see cref="ICollectionExporter"/> in the
/// export picker. Exposes <see cref="DisplayName"/> and the
/// <see cref="IsVerified"/> / <see cref="IsUnverified"/> pair for the
/// unverified badge — no format-specific code lives here; the UI iterates
/// a generic list of these.
/// </summary>
public sealed class ExporterItem
{
    public ExporterItem(ICollectionExporter exporter)
    {
        ArgumentNullException.ThrowIfNull(exporter);
        Exporter = exporter;
    }

    /// <summary>The underlying exporter, passed to <see cref="CollectionViewModel.ExportToStreamAsync"/>.</summary>
    public ICollectionExporter Exporter { get; }

    /// <inheritdoc cref="ExportFormat.DisplayName"/>
    public string DisplayName => Exporter.Format.DisplayName;

    /// <inheritdoc cref="ExportFormat.IsVerified"/>
    public bool IsVerified => Exporter.Format.IsVerified;

    /// <summary>
    /// True when the format has NOT been verified with a live tool. Bound to
    /// the visibility of the "unverified" badge in the export picker's item
    /// template.
    /// </summary>
    public bool IsUnverified => !IsVerified;

    /// <summary>Display name used when the item appears in a plain text context.</summary>
    public override string ToString() => DisplayName;
}
