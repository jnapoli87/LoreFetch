namespace LoreFetch.Core.Collection;

/// <summary>
/// Thrown when the native CSV format is violated: an unknown or missing
/// header column (the header's exact column SET is the format version —
/// DECISIONS.md §Storage and docs/design/collection.md §D1), a data row with the
/// wrong field count, or a field that fails to parse into its typed
/// <see cref="Abstractions.CollectionRow"/> member.
///
/// Deliberately distinct from <c>CollectionStoreException</c>
/// (Core/Abstractions), which is about I/O — the file could not be read or
/// replaced, most often because another program has it open. This one is
/// about the CONTENT being malformed once the bytes were read
/// successfully. The codec never silently drops or "fixes" a line that
/// doesn't parse — docs/TESTING.md's CSV-store case is explicit: "a
/// malformed line is reported, not silently dropped." A future store
/// (built on this codec) can catch and wrap this into a
/// <c>CollectionStoreException</c> if that is the right surface for a
/// caller; the codec itself only ever reports.
/// </summary>
public sealed class NativeCsvFormatException : FormatException
{
    public NativeCsvFormatException(string message)
        : base(message)
    {
    }

    public NativeCsvFormatException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
