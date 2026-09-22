using System.Security.Cryptography;
using System.Text;
using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Imaging;

namespace LoreFetch.Core.Identification;

/// One entry of the committed hash index: a card's perceptual hash plus
/// enough identity to answer a query -- see stream-b-identification.md
/// "B3 -- HashIndex" and CONTRACTS.md "Identification" for what OracleId
/// and ArtworkId mean. OracleId/OracleName are denormalized here for a
/// convenient in-memory shape; on disk each distinct oracle card is stored
/// once, in the oracle table, and referenced by index -- with ~1.45 arts
/// per oracle card on average, repeating the name on every entry would cost
/// real bytes for nothing.
public readonly record struct HashIndexEntry(
    CardHash Hash,
    string OracleId,
    string OracleName,
    string ArtworkId,
    bool IsBasicLand);

/// A loaded index: entries plus the deduplicated oracle table. Kept as two
/// separate lists because `IOracleCatalog.All` (B3b) wants every DISTINCT
/// `(OracleId, OracleName)` exactly once, not once per artwork -- exposing
/// the table `Read` already built avoids a `Distinct()` over tens of
/// thousands of entries just to get back what the file already knew.
public sealed class HashIndexData
{
    public HashIndexData(IReadOnlyList<HashIndexEntry> entries, IReadOnlyList<OracleEntry> oracleTable)
    {
        Entries = entries;
        OracleTable = oracleTable;
    }

    public IReadOnlyList<HashIndexEntry> Entries { get; }

    public IReadOnlyList<OracleEntry> OracleTable { get; }
}

/// Thrown by `HashIndexFile.Read` for anything that makes the file
/// unusable: wrong magic, an unsupported format version, or a file that
/// ends before its own header says it should (truncation). Deliberately
/// ONE exception type for all of these -- a caller wants to know "this
/// index cannot be loaded", not which of several BCL exception types
/// (`EndOfStreamException`, `IOException`, ...) happened to leak out of a
/// `BinaryReader` this time. No path through `Read` throws anything else.
public sealed class HashIndexFormatException : Exception
{
    public HashIndexFormatException(string message)
        : base(message)
    {
    }

    public HashIndexFormatException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// Reads and writes `cards.lfidx`, the committed hash index's binary format
/// (stream-b-identification.md "B3 -- HashIndex"; orchestration-plan.md
/// V11 for the `IsBasicLand` flag, which is what lets B6 exclude lands from
/// the accuracy table by data the builder already has, rather than a
/// `type_line` string check repeated at every call site).
///
/// Layout: header (magic, format version, entry count, oracle count), then
/// the oracle table (`OracleId` + `OracleName`, once per distinct oracle
/// card), then one record per hash entry: 16 `ulong` hash words, an index
/// into the oracle table, the entry's own `ArtworkId`, and a 1-byte
/// `IsBasicLand` flag.
///
/// Little-endian throughout: `BinaryWriter`/`BinaryReader` write and read
/// their primitives (`int`, `ulong`) little-endian on every platform
/// LoreFetch targets. That is documented .NET behaviour, not an assumption
/// this type is making -- called out here because the file's byte layout
/// depends on it.
///
/// Strings are length-prefixed UTF-8, with an explicit 4-byte
/// little-endian length written by this type -- deliberately NOT
/// `BinaryWriter.Write(string)` / `BinaryReader.ReadString()`, which use
/// their own 7-bit-encoded ("ULEB128-style") length prefix. That format
/// isn't wrong, but hand-writing the length keeps the file's entire byte
/// layout specified by this one type, rather than partly by a BCL encoding
/// this type doesn't own and would have to explain rather than define.
///
/// The reader validates magic and format version before trusting anything
/// else in the file, and treats a stream that ends early as corruption --
/// never as a silently partial load.
public static class HashIndexFile
{
    public const int SupportedFormatVersion = 1;

    // "LFIX" -- LoreFetch Index. Not a meaningful acronym beyond that; it
    // only needs to be distinctive enough that an unrelated file is
    // unlikely to start with these four bytes by chance.
    private static readonly byte[] Magic = "LFIX"u8.ToArray();

    public static void Write(string path, IReadOnlyList<HashIndexEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var oracleTable = BuildOracleTable(entries, out var oracleIndexByOracleId);

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        writer.Write(Magic);
        writer.Write(SupportedFormatVersion);
        writer.Write(entries.Count);
        writer.Write(oracleTable.Count);

        foreach (var oracle in oracleTable)
        {
            WriteString(writer, oracle.OracleId);
            WriteString(writer, oracle.OracleName);
        }

        foreach (var entry in entries)
        {
            foreach (var word in entry.Hash.Words)
            {
                writer.Write(word);
            }

            writer.Write(oracleIndexByOracleId[entry.OracleId]);
            WriteString(writer, entry.ArtworkId);
            writer.Write(entry.IsBasicLand ? (byte)1 : (byte)0);
        }
    }

    public static HashIndexData Read(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Hash index file not found: {path}", path);
        }

        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        try
        {
            var magic = reader.ReadBytes(Magic.Length);
            if (magic.Length != Magic.Length || !magic.AsSpan().SequenceEqual(Magic))
            {
                throw new HashIndexFormatException($"'{path}' is not a LoreFetch hash index (bad magic).");
            }

            var formatVersion = reader.ReadInt32();
            if (formatVersion != SupportedFormatVersion)
            {
                throw new HashIndexFormatException(
                    $"'{path}' has unsupported format version {formatVersion}; " +
                    $"this build only understands {SupportedFormatVersion}.");
            }

            var entryCount = reader.ReadInt32();
            var oracleCount = reader.ReadInt32();
            if (entryCount < 0 || oracleCount < 0)
            {
                throw new HashIndexFormatException($"'{path}' has a negative count in its header.");
            }

            var oracleTable = new List<OracleEntry>(oracleCount);
            for (var i = 0; i < oracleCount; i++)
            {
                var oracleId = ReadString(reader, path);
                var oracleName = ReadString(reader, path);
                oracleTable.Add(new OracleEntry(oracleId, oracleName));
            }

            var entries = new List<HashIndexEntry>(entryCount);
            var words = new ulong[CardHash.WordCount];
            for (var i = 0; i < entryCount; i++)
            {
                for (var w = 0; w < CardHash.WordCount; w++)
                {
                    words[w] = reader.ReadUInt64();
                }

                var oracleIndex = reader.ReadInt32();
                if (oracleIndex < 0 || oracleIndex >= oracleTable.Count)
                {
                    throw new HashIndexFormatException(
                        $"'{path}' entry {i} references oracle index {oracleIndex}, outside the table of {oracleTable.Count}.");
                }

                var artworkId = ReadString(reader, path);
                var isBasicLand = reader.ReadByte() != 0;

                var oracle = oracleTable[oracleIndex];
                entries.Add(new HashIndexEntry(new CardHash(words), oracle.OracleId, oracle.OracleName, artworkId, isBasicLand));
            }

            return new HashIndexData(entries, oracleTable);
        }
        catch (EndOfStreamException ex)
        {
            // Every fixed-width read (magic aside, which checks its own
            // length) goes through here on a stream that ends mid-field --
            // one place to turn "BinaryReader ran out of bytes" into the
            // one exception type this method promises.
            throw new HashIndexFormatException($"'{path}' ends before its header says it should (truncated).", ex);
        }
    }

    /// SHA-256 of the file's raw bytes, lowercase hex -- the form
    /// `ThresholdsFile.IndexSha256` (Core/Scanning, not edited here)
    /// expects. Optional convenience: nothing in this type requires a
    /// caller to use it.
    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexStringLower(hash);
    }

    /// Builds the deduplicated oracle table in first-seen order, and
    /// rejects the one way the caller's data could be self-contradictory:
    /// the same `OracleId` appearing with two different `OracleName`
    /// values, which would make "the" name for that oracle card ambiguous
    /// on disk.
    private static List<OracleEntry> BuildOracleTable(
        IReadOnlyList<HashIndexEntry> entries, out Dictionary<string, int> oracleIndexByOracleId)
    {
        oracleIndexByOracleId = new Dictionary<string, int>(StringComparer.Ordinal);
        var oracleTable = new List<OracleEntry>();

        foreach (var entry in entries)
        {
            ValidateEntry(entry);

            if (oracleIndexByOracleId.TryGetValue(entry.OracleId, out var existingIndex))
            {
                var existingName = oracleTable[existingIndex].OracleName;
                if (!string.Equals(existingName, entry.OracleName, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"OracleId '{entry.OracleId}' maps to two different names: '{existingName}' and '{entry.OracleName}'.",
                        nameof(entries));
                }

                continue;
            }

            oracleIndexByOracleId.Add(entry.OracleId, oracleTable.Count);
            oracleTable.Add(new OracleEntry(entry.OracleId, entry.OracleName));
        }

        return oracleTable;
    }

    private static void ValidateEntry(HashIndexEntry entry)
    {
        if (string.IsNullOrEmpty(entry.OracleId))
        {
            throw new ArgumentException("A hash index entry must have a non-empty OracleId.", nameof(entry));
        }

        if (string.IsNullOrEmpty(entry.OracleName))
        {
            throw new ArgumentException("A hash index entry must have a non-empty OracleName.", nameof(entry));
        }

        if (string.IsNullOrEmpty(entry.ArtworkId))
        {
            throw new ArgumentException("A hash index entry must have a non-empty ArtworkId.", nameof(entry));
        }
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadString(BinaryReader reader, string path)
    {
        var length = reader.ReadInt32();
        if (length < 0)
        {
            throw new HashIndexFormatException($"'{path}' has a negative string length ({length}).");
        }

        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new HashIndexFormatException($"'{path}' ends before a {length}-byte string finished reading (truncated).");
        }

        return Encoding.UTF8.GetString(bytes);
    }
}
