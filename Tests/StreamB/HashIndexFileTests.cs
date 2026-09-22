using LoreFetch.Core.Identification;
using LoreFetch.Core.Imaging;
using Xunit;

namespace LoreFetch.Tests.StreamB;

/// Round-trip and corruption-handling tests for `cards.lfidx`. Uses
/// synthetic, deterministic entries -- arbitrary hash bits, made-up ids --
/// because these tests are about the FILE FORMAT, not the hash algorithm,
/// so there is nothing here that needs a real card or a real image.
public class HashIndexFileTests
{
    private const int OracleCount = 300;
    private const int EntryCount = 1000;

    /// Deliberately includes a non-ASCII oracle name -- "Lim-Dûl's Vault"
    /// is a real Magic card name and an easy way for a naive
    /// ASCII/UTF-16 string encoding to silently corrupt data that a
    /// round-trip test over ASCII-only names would never catch.
    private const string NonAsciiOracleName = "Lim-Dûl's Vault";
    private const int NonAsciiOracleIndex = 7;

    [Fact]
    public void RoundTrip_EmptyIndex_Succeeds()
    {
        var path = TempPath();
        try
        {
            HashIndexFile.Write(path, Array.Empty<HashIndexEntry>());

            var data = HashIndexFile.Read(path);

            Assert.Empty(data.Entries);
            Assert.Empty(data.OracleTable);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RoundTrip_OneThousandEntriesOverThreeHundredOracleIds_PreservesEverythingExactly()
    {
        var path = TempPath();
        try
        {
            var written = BuildSyntheticEntries();
            HashIndexFile.Write(path, written);

            var data = HashIndexFile.Read(path);

            Assert.Equal(EntryCount, data.Entries.Count);
            Assert.Equal(OracleCount, data.OracleTable.Count);

            for (var i = 0; i < written.Count; i++)
            {
                var expected = written[i];
                var actual = data.Entries[i];

                Assert.Equal(expected.Hash, actual.Hash);
                Assert.Equal(expected.OracleId, actual.OracleId);
                Assert.Equal(expected.OracleName, actual.OracleName);
                Assert.Equal(expected.ArtworkId, actual.ArtworkId);
                Assert.Equal(expected.IsBasicLand, actual.IsBasicLand);
            }

            // The non-ASCII name specifically, not just "some name matched":
            // an encoding bug can corrupt exactly the characters outside
            // ASCII while leaving every other name in the table intact.
            var nonAsciiOracleId = OracleId(NonAsciiOracleIndex);
            var roundTrippedName = data.OracleTable.Single(o => o.OracleId == nonAsciiOracleId).OracleName;
            Assert.Equal(NonAsciiOracleName, roundTrippedName);

            // At least one basic land and one non-land, so the flag is
            // proven both ways, not just "never true" or "always true".
            Assert.Contains(data.Entries, e => e.IsBasicLand);
            Assert.Contains(data.Entries, e => !e.IsBasicLand);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Read_WrongMagic_Throws()
    {
        var path = TempPath();
        try
        {
            HashIndexFile.Write(path, BuildSyntheticEntries());

            var bytes = File.ReadAllBytes(path);
            bytes[0] ^= 0xFF; // corrupt exactly the magic, nothing else
            File.WriteAllBytes(path, bytes);

            Assert.Throws<HashIndexFormatException>(() => HashIndexFile.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Read_WrongVersion_Throws()
    {
        var path = TempPath();
        try
        {
            HashIndexFile.Write(path, BuildSyntheticEntries());

            var bytes = File.ReadAllBytes(path);
            // Format version is the first int32 after the 4-byte magic.
            var versionOffset = 4;
            BitConverter.GetBytes(999).CopyTo(bytes, versionOffset);
            File.WriteAllBytes(path, bytes);

            var ex = Assert.Throws<HashIndexFormatException>(() => HashIndexFile.Read(path));
            Assert.Contains("999", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(0)] // cut off inside the header itself
    [InlineData(20)] // cut off inside the oracle table
    [InlineData(5000)] // cut off inside the entry records
    public void Read_TruncatedFile_Throws(int keepBytes)
    {
        var path = TempPath();
        try
        {
            HashIndexFile.Write(path, BuildSyntheticEntries());

            var bytes = File.ReadAllBytes(path);
            var truncated = bytes.AsSpan(0, Math.Min(keepBytes, bytes.Length)).ToArray();
            File.WriteAllBytes(path, truncated);

            Assert.Throws<HashIndexFormatException>(() => HashIndexFile.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Write_InconsistentOracleName_Throws()
    {
        var entries = new List<HashIndexEntry>
        {
            MakeEntry(0, "oracle-0", "Alpha", "art-0", isBasicLand: false),
            MakeEntry(1, "oracle-0", "Beta", "art-1", isBasicLand: false), // same id, different name
        };

        var path = TempPath();
        try
        {
            Assert.Throws<ArgumentException>(() => HashIndexFile.Write(path, entries));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static List<HashIndexEntry> BuildSyntheticEntries()
    {
        var entries = new List<HashIndexEntry>(EntryCount);
        for (var i = 0; i < EntryCount; i++)
        {
            var oracleIndex = i % OracleCount;
            var oracleName = oracleIndex == NonAsciiOracleIndex ? NonAsciiOracleName : $"Card {oracleIndex}";

            // A couple of dedicated oracle ids stand in for basic lands, so
            // "IsBasicLand" doesn't just happen to correlate with any other
            // field -- the same oracle id/name combination appears with the
            // flag both set and clear across its different arts, matching
            // how a real land's many arts all share one oracle name.
            var isBasicLand = oracleIndex is 1 or 2;

            entries.Add(MakeEntry(i, OracleId(oracleIndex), oracleName, ArtworkId(i), isBasicLand));
        }

        return entries;
    }

    private static HashIndexEntry MakeEntry(int seed, string oracleId, string oracleName, string artworkId, bool isBasicLand)
    {
        var words = new ulong[CardHash.WordCount];
        for (var w = 0; w < words.Length; w++)
        {
            // Arbitrary but deterministic and distinct per (seed, word) --
            // a real Hamming-distance-friendly hash isn't needed here, only
            // bytes that survive a round trip unchanged.
            words[w] = unchecked((ulong)(seed + 1) * 0x9E3779B97F4A7C15UL) ^ ((ulong)w << 32);
        }

        return new HashIndexEntry(new CardHash(words), oracleId, oracleName, artworkId, isBasicLand);
    }

    private static string OracleId(int index) => $"oracle-{index:D4}";

    private static string ArtworkId(int index) => $"art-{index:D4}";

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"lorefetch-test-{Guid.NewGuid():N}.lfidx");
}
