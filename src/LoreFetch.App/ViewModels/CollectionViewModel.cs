using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LoreFetch.Core.Abstractions;

namespace LoreFetch.App.ViewModels;

/// <summary>
/// View model for the A8 collection DataGrid and export picker.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Rows"/> is the live collection bound to the DataGrid;
/// <see cref="LoadAsync"/> fetches the current rows from the store.
/// </para>
/// <para>
/// <see cref="ExportToStreamAsync"/> is the testable export entry point:
/// it takes the chosen <see cref="ICollectionExporter"/> and a
/// <see cref="Stream"/> the caller opened (so tests pass a
/// <see cref="System.IO.MemoryStream"/> without a save-file dialog).
/// The code-behind opens the save-file dialog and supplies the stream.
/// </para>
/// <para>
/// Threading: <see cref="LoadAsync"/> is designed to be awaited from the
/// UI thread — no <c>ConfigureAwait(false)</c> so the continuation (which
/// mutates the <see cref="ObservableCollection{T}"/>) remains on the
/// caller's synchronisation context. For plain-<c>[Fact]</c> unit tests
/// where no Avalonia event loop runs, <c>StubCollectionStore.ListAsync</c>
/// completes synchronously, so no thread switch happens anyway.
/// </para>
/// </remarks>
public sealed class CollectionViewModel : ObservableObject
{
    private readonly ICollectionStore? _store;

    /// <param name="store">
    /// Collection store to read from. When <c>null</c>, <see cref="LoadAsync"/>
    /// and <see cref="ExportToStreamAsync"/> are no-ops (empty rows).
    /// Optional so test constructors need not supply one.
    /// </param>
    /// <param name="exporters">
    /// The exporters to populate <see cref="ExporterItems"/>. Optional;
    /// defaults to an empty list.
    /// </param>
    public CollectionViewModel(
        ICollectionStore? store = null,
        IReadOnlyList<ICollectionExporter>? exporters = null)
    {
        _store = store;
        ExporterItems = (exporters ?? [])
            .Select(e => new ExporterItem(e))
            .ToList();
        Rows = [];

        // A9: fire IsEmpty / HasRows whenever the observable collection changes.
        Rows.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasRows));
        };
    }

    /// <summary>
    /// Rows from the collection store, bound to the DataGrid. Populated by
    /// <see cref="LoadAsync"/>; empty until that is called.
    /// </summary>
    public ObservableCollection<CollectionRow> Rows { get; }

    /// <summary>
    /// True when <see cref="Rows"/> is empty. Bound to the empty-collection
    /// placeholder visibility (A9).
    /// </summary>
    public bool IsEmpty => Rows.Count == 0;

    /// <summary>
    /// True when <see cref="Rows"/> has at least one row. Inverse of
    /// <see cref="IsEmpty"/>; exposed so XAML can bind either direction.
    /// </summary>
    public bool HasRows => Rows.Count > 0;

    /// <summary>
    /// One <see cref="ExporterItem"/> per registered
    /// <see cref="ICollectionExporter"/>, including the
    /// <see cref="ExporterItem.IsVerified"/> / <see cref="ExporterItem.IsUnverified"/>
    /// flags the UI uses to render the badge.
    /// </summary>
    public IReadOnlyList<ExporterItem> ExporterItems { get; }

    /// <summary>
    /// Fetches the current rows from the store and replaces
    /// <see cref="Rows"/>. Must be awaited on the UI thread so the
    /// <see cref="ObservableCollection{T}"/> mutations that follow do not
    /// cross a thread boundary while the DataGrid observes them.
    /// Returns immediately when no store is wired.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        if (_store is null) return;

        // No ConfigureAwait(false) — continuation stays on the UI thread so
        // Rows.Clear() / Rows.Add() run there and the DataGrid binding is safe.
        var rows = await _store.ListAsync(ct);
        Rows.Clear();
        foreach (var row in rows)
            Rows.Add(row);
    }

    /// <summary>
    /// Exports the current collection via <paramref name="exporter"/> into
    /// <paramref name="destination"/>. Fetches a fresh snapshot from the
    /// store immediately before writing so the file reflects committed rows
    /// even if <see cref="LoadAsync"/> has not been called recently. The
    /// caller opened <paramref name="destination"/> and owns its lifetime —
    /// the exporter writes to it with <c>leaveOpen: true</c>.
    /// </summary>
    /// <remarks>
    /// This is the testable entry point: tests pass a
    /// <see cref="System.IO.MemoryStream"/> here directly, bypassing the
    /// save-file dialog that the code-behind opens.
    /// </remarks>
    public async Task ExportToStreamAsync(
        ICollectionExporter exporter,
        Stream destination,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(exporter);
        ArgumentNullException.ThrowIfNull(destination);

        var rows = _store is not null
            ? await _store.ListAsync(ct).ConfigureAwait(false)
            : (IReadOnlyList<CollectionRow>)[];

        await exporter.ExportAsync(rows, destination, ct).ConfigureAwait(false);
    }
}
