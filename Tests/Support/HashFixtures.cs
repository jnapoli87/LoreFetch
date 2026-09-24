using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Identification;
using LoreFetch.Core.Imaging;

namespace LoreFetch.Tests.Support;

/// Small helpers for building `HashIndexData` and `CardHash` values with
/// EXACTLY known properties, shared by `HashCardIdentifierTests` and
/// `HashCardIdentifierPerformanceTests`. Deliberately separate from
/// `SyntheticImages` (which is about pixels); these are about hash bits and
/// index shape, and have nothing to do with an image.
internal static class HashFixtures
{
    /// A new hash equal to `baseHash` with exactly the bits at `bitIndices`
    /// toggled (each index in `[0, CardHash.BitCount)`). The Hamming
    /// distance from `baseHash` to the result is exactly
    /// `bitIndices.Distinct().Count()` -- callers pass distinct indices so
    /// that count is just `bitIndices.Length`, which is how the B3b tests
    /// build entries at precisely known distances from a query hash.
    public static CardHash FlipBits(CardHash baseHash, params int[] bitIndices)
    {
        var words = baseHash.Words.ToArray();
        foreach (var bit in bitIndices)
        {
            var wordIndex = bit / 64;
            var bitInWord = bit % 64;
            words[wordIndex] ^= 1UL << bitInWord;
        }

        return new CardHash(words);
    }

    /// Convenience over `FlipBits` for "I just want a hash exactly `n` bits
    /// away from `baseHash`, I don't care which bits" -- flips bits
    /// `0..n-1`. `n` must be at most `CardHash.BitCount`.
    public static CardHash FlipFirstNBits(CardHash baseHash, int n)
    {
        var bits = new int[n];
        for (var i = 0; i < n; i++)
        {
            bits[i] = i;
        }

        return FlipBits(baseHash, bits);
    }

    /// Builds a `HashIndexData` from entries alone, deriving the oracle
    /// table the same way `HashIndexFile.Write` does: one row per distinct
    /// `OracleId`, in first-seen order. Tests that care about the tie-break
    /// order `HashCardIdentifier.Identify` uses (oracle-table index) control
    /// it by controlling the order they list entries in.
    public static HashIndexData BuildIndexData(IReadOnlyList<HashIndexEntry> entries)
    {
        var oracleTable = new List<OracleEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            if (seen.Add(entry.OracleId))
            {
                oracleTable.Add(new OracleEntry(entry.OracleId, entry.OracleName));
            }
        }

        return new HashIndexData(entries, oracleTable);
    }
}
