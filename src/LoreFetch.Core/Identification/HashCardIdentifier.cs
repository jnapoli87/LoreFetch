using System.Numerics;
using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Imaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;

namespace LoreFetch.Core.Identification;

/// `ICardIdentifier` over a loaded `cards.lfidx`: brute-force full 1024-bit
/// Hamming search, no early rejection, no threshold -- see CLAUDE.md "Step 7:
/// do not port upstream's early rejection" and `CardHash`'s own doc comment.
/// Also `IOracleCatalog`, backed by the same loaded table, since both read
/// off the same index and a second type would just be a second copy of
/// `HashIndexData.OracleTable`.
///
/// The query path is exactly `QueryTransform.Prepare` -> `CardHasher.Hash` --
/// no blur, no resize added here, matching CLAUDE.md's asymmetric-sides rule
/// (see `QueryTransform`'s own doc comment). The one thing this type adds on
/// top of that path is orientation: a card laid on the mat 180 degrees round
/// rectifies to a perfectly valid quad (`TL`/`TR`/`BR`/`BL` are geometric, not
/// semantic -- stream-b-identification.md "B5", "Card orientation is
/// unhandled"), so every query is hashed both upright and rotated 180
/// degrees, and the better of the two distances wins. The flip runs on the
/// full-card grayscale `QueryTransform.Prepare` produces, BEFORE
/// `CardHasher`'s region crop -- flipping after the crop would keep the
/// (now-upside-down) top-61% region instead of moving to the card's true top.
///
/// Storage is two flat, contiguous arrays sized to the entry count --
/// `_flatWords` (16 `ulong` per entry) and `_entryOracleIndex` -- rather than
/// an array of `CardHash`/`HashIndexEntry` structs, so the per-query scan
/// walks one cache-friendly buffer instead of chasing 55k boxed-looking
/// entries. Brute force is still the design (CLAUDE.md measures 0.243 ms per
/// query over 55k entries); this only keeps that scan cheap, it does not
/// change what gets searched.
public sealed class HashCardIdentifier : ICardIdentifier, IOracleCatalog
{
    /// `ICardIdentifier.Name` -- identifies this implementation in logs and
    /// the accuracy table.
    public const string IdentifierName = "CardSpotterHash/v1";

    private readonly ulong[] _flatWords; // entryCount * CardHash.WordCount
    private readonly int[] _entryOracleIndex; // one per entry, indexes _oracleTable
    private readonly string[] _entryArtworkId; // one per entry
    private readonly int _entryCount;
    private readonly IReadOnlyList<OracleEntry> _oracleTable;
    private readonly ILogger _logger;

    /// Builds directly from an already-loaded (or in-test-constructed)
    /// index. `Load` is the blessed path for real use; this constructor
    /// stays public so tests -- and B7's index-builder tests -- can hand in
    /// a `HashIndexData` built in memory, with no file on disk at all.
    public HashCardIdentifier(HashIndexData data, ILogger<HashCardIdentifier>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(data);

        _logger = logger ?? NullLogger<HashCardIdentifier>.Instance;
        _oracleTable = data.OracleTable;

        var oracleIndexByOracleId = new Dictionary<string, int>(_oracleTable.Count, StringComparer.Ordinal);
        for (var i = 0; i < _oracleTable.Count; i++)
        {
            oracleIndexByOracleId[_oracleTable[i].OracleId] = i;
        }

        _entryCount = data.Entries.Count;
        _flatWords = new ulong[_entryCount * CardHash.WordCount];
        _entryOracleIndex = new int[_entryCount];
        _entryArtworkId = new string[_entryCount];

        for (var i = 0; i < _entryCount; i++)
        {
            var entry = data.Entries[i];
            if (!oracleIndexByOracleId.TryGetValue(entry.OracleId, out var oracleIndex))
            {
                throw new ArgumentException(
                    $"Entry {i} references OracleId '{entry.OracleId}', which is not in the index's oracle table.",
                    nameof(data));
            }

            entry.Hash.Words.CopyTo(_flatWords.AsSpan(i * CardHash.WordCount, CardHash.WordCount));
            _entryOracleIndex[i] = oracleIndex;
            _entryArtworkId[i] = entry.ArtworkId;
        }
    }

    /// Reads `indexPath` via `HashIndexFile.Read` and wraps it. The blessed
    /// construction path for real use (the app, the accuracy harness); tests
    /// that already have a `HashIndexData` in hand should use the
    /// constructor directly rather than round-tripping through a temp file.
    public static HashCardIdentifier Load(string indexPath, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(indexPath);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var data = HashIndexFile.Read(indexPath);
        var logger = loggerFactory.CreateLogger<HashCardIdentifier>();
        var identifier = new HashCardIdentifier(data, logger);

        logger.LogInformation(
            "Loaded hash index from {Path}: {EntryCount} entries, {OracleCount} oracle cards.",
            indexPath, data.Entries.Count, data.OracleTable.Count);

        return identifier;
    }

    public string Name => IdentifierName;

    /// Every distinct oracle card the loaded index carries -- exactly
    /// `HashIndexData.OracleTable`, which is already deduplicated to one
    /// `(OracleId, OracleName)` per oracle card (see `HashIndexData`'s own
    /// doc comment).
    public IReadOnlyList<OracleEntry> All => _oracleTable;

    /// Full brute-force scan: every entry, both query orientations, no
    /// early exit and no distance threshold -- see this type's own doc
    /// comment and CLAUDE.md "Step 7". Returns the nearest `maxCandidates`
    /// DISTINCT `OracleId`s, best distance per oracle across all of its
    /// arts and both orientations, ascending by distance. Ties break by the
    /// oracle's position in the index's own oracle table (i.e. first-seen
    /// order when the index was built) -- arbitrary but deterministic and
    /// stable across runs of the same index.
    ///
    /// `maxCandidates` must be positive; zero or negative throws
    /// `ArgumentOutOfRangeException` rather than silently returning nothing
    /// or the whole table -- there is no sensible "candidateless" call site.
    /// Empty only when the index itself has no entries.
    public IReadOnlyList<CardCandidate> Identify(RectifiedCard card, int maxCandidates)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (maxCandidates <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxCandidates), maxCandidates, "maxCandidates must be positive.");
        }

        if (_entryCount == 0)
        {
            return Array.Empty<CardCandidate>();
        }

        Span<ulong> uprightWords = stackalloc ulong[CardHash.WordCount];
        Span<ulong> flippedWords = stackalloc ulong[CardHash.WordCount];
        HashBothOrientations(card, uprightWords, flippedWords);

        var bestDistancePerOracle = new int[_oracleTable.Count];
        var bestArtworkPerOracle = new string?[_oracleTable.Count];
        Array.Fill(bestDistancePerOracle, int.MaxValue);

        for (var entryIndex = 0; entryIndex < _entryCount; entryIndex++)
        {
            var entryWords = _flatWords.AsSpan(entryIndex * CardHash.WordCount, CardHash.WordCount);

            var uprightDistance = HammingDistance(entryWords, uprightWords);
            var flippedDistance = HammingDistance(entryWords, flippedWords);
            var distance = Math.Min(uprightDistance, flippedDistance);

            var oracleIndex = _entryOracleIndex[entryIndex];
            if (distance < bestDistancePerOracle[oracleIndex])
            {
                bestDistancePerOracle[oracleIndex] = distance;
                bestArtworkPerOracle[oracleIndex] = _entryArtworkId[entryIndex];
            }
        }

        return RankOracles(bestDistancePerOracle, bestArtworkPerOracle, maxCandidates);
    }

    /// Query side, both orientations: `QueryTransform.Prepare` (no blur, no
    /// resize -- CLAUDE.md's asymmetric-sides rule) then, for the 180-degree
    /// variant, `Cv2.Flip` on that SAME full-card grayscale before either
    /// one reaches `CardHasher` -- flipping the already-cropped icon would
    /// leave the region crop pointed at the old top of the card instead of
    /// following the card's rotation. `Cv2.Flip(..., FlipMode.XY)` flips
    /// both axes, matching upstream's `cv::flip(card, card, -1)`
    /// (stream-b-identification.md "B5").
    private static void HashBothOrientations(RectifiedCard card, Span<ulong> uprightWords, Span<ulong> flippedWords)
    {
        using var gray = QueryTransform.Prepare(card);
        CardHasher.Hash(gray).Words.CopyTo(uprightWords);

        using var flippedGray = new Mat();
        Cv2.Flip(gray, flippedGray, FlipMode.XY);
        CardHasher.Hash(flippedGray).Words.CopyTo(flippedWords);
    }

    /// Word-by-word popcount over two flat 16-`ulong` hash spans -- the same
    /// arithmetic as `CardHash.HammingDistance`, inlined here against the
    /// flat storage so the scan never has to materialize a `CardHash` per
    /// entry just to compare it.
    private static int HammingDistance(ReadOnlySpan<ulong> a, ReadOnlySpan<ulong> b)
    {
        var distance = 0;
        for (var i = 0; i < CardHash.WordCount; i++)
        {
            distance += BitOperations.PopCount(a[i] ^ b[i]);
        }

        return distance;
    }

    /// Collapses the per-oracle best distances to the nearest `maxCandidates`
    /// distinct oracles, ascending by distance, oracle-table-index as the
    /// tie-break. An oracle with no entry at all (distance still
    /// `int.MaxValue`) is excluded rather than surfaced at the bottom of the
    /// ranking -- it was never actually searched.
    ///
    /// Bounded top-K by sorted insertion, not a full sort: `maxCandidates`
    /// is at most single digits in practice (CandidatesPerTile in
    /// Core/Scanning), while the oracle table is tens of thousands of rows,
    /// so sorting the whole table just to keep its first few entries was
    /// pure waste -- measured at ~9 ms/query of a 13 ms total against a
    /// realistic index, almost all of it in `List.Sort`'s delegate calls
    /// over an oracle table two orders of magnitude bigger than what the
    /// caller keeps. This is a SELECTION algorithm, not a filter: every
    /// oracle slot is still visited exactly once (the loop below), and a
    /// candidate is only ever dropped for being worse than all `capacity`
    /// slots already kept -- never because of a distance threshold. See
    /// CLAUDE.md "Step 7" (`ICardIdentifier.Identify` never filters by
    /// threshold) -- that invariant is about the SET of results, and this
    /// only changes how that set is picked.
    private IReadOnlyList<CardCandidate> RankOracles(
        int[] bestDistancePerOracle, string?[] bestArtworkPerOracle, int maxCandidates)
    {
        var capacity = Math.Min(maxCandidates, _oracleTable.Count);
        if (capacity == 0)
        {
            return Array.Empty<CardCandidate>();
        }

        // Parallel arrays, ascending by (Distance, OracleIndex), kept sorted
        // as entries are inserted -- `count` is how many of the `capacity`
        // slots are filled so far (grows to `capacity` and then stays there
        // as further candidates only ever displace the current worst).
        var topDistances = new int[capacity];
        var topOracleIndices = new int[capacity];
        var count = 0;

        for (var oracleIndex = 0; oracleIndex < _oracleTable.Count; oracleIndex++)
        {
            var distance = bestDistancePerOracle[oracleIndex];
            if (distance == int.MaxValue)
            {
                continue; // no entry ever referenced this oracle -- it was never searched
            }

            if (count < capacity)
            {
                InsertSorted(topDistances, topOracleIndices, count, distance, oracleIndex);
                count++;
            }
            else if (IsBetter(distance, oracleIndex, topDistances[capacity - 1], topOracleIndices[capacity - 1]))
            {
                // Displaces the current worst kept candidate, then re-sorts
                // into place -- the buffer never grows past `capacity`.
                InsertSorted(topDistances, topOracleIndices, capacity - 1, distance, oracleIndex);
            }
        }

        var result = new List<CardCandidate>(count);
        for (var i = 0; i < count; i++)
        {
            var oracleIndex = topOracleIndices[i];
            var oracle = _oracleTable[oracleIndex];
            result.Add(new CardCandidate(oracle.OracleId, oracle.OracleName, topDistances[i], bestArtworkPerOracle[oracleIndex]));
        }

        return result;
    }

    /// Shifts entries at and after `atIndex` right by one, then writes
    /// `(distance, oracleIndex)` into its sorted position at or before
    /// `atIndex` -- the shared insertion step `RankOracles` uses both to
    /// grow the buffer (`atIndex == count`, nothing to overwrite yet) and to
    /// replace its current worst slot (`atIndex == capacity - 1`).
    private static void InsertSorted(int[] distances, int[] oracleIndices, int atIndex, int distance, int oracleIndex)
    {
        var insertAt = atIndex;
        while (insertAt > 0 && IsBetter(distance, oracleIndex, distances[insertAt - 1], oracleIndices[insertAt - 1]))
        {
            distances[insertAt] = distances[insertAt - 1];
            oracleIndices[insertAt] = oracleIndices[insertAt - 1];
            insertAt--;
        }

        distances[insertAt] = distance;
        oracleIndices[insertAt] = oracleIndex;
    }

    /// The ranking's tie-break, as one place both insertion call sites
    /// share: ascending distance, then ascending oracle-table index.
    private static bool IsBetter(int distanceA, int oracleIndexA, int distanceB, int oracleIndexB) =>
        distanceA != distanceB ? distanceA < distanceB : oracleIndexA < oracleIndexB;
}
