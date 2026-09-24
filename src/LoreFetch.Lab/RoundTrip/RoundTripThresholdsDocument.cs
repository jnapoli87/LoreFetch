using System.Text.Json;
using System.Text.Json.Nodes;

namespace LoreFetch.Lab.RoundTrip;

/// `data/index/thresholds.json`'s B2 contribution -- serialized with
/// `JsonNamingPolicy.CamelCase` (matching `Core/Scanning/ThresholdsFile`'s
/// own reader exactly, field for field, on every property this type
/// shares with it: `formatVersion`, `referenceFloor`, `indexArtworkCount`,
/// `indexSha256`, `measuredAt`, `notes`), plus B2-specific provenance and
/// margin fields the frozen reader does not know about and silently
/// ignores (`System.Text.Json.Deserialize` drops unmapped JSON properties
/// by default).
///
/// Deliberately WITHOUT `goodDistance`/`okDistance`: those are B6's job
/// (calibrated from the real fixture corpus, gated on H3), not B2's, and
/// `ThresholdsFile.Load`'s own doc comment calls its whole design "loud
/// rather than lenient" -- a missing required field throws
/// `InvalidDataException` naming exactly which field is absent, which is
/// the CORRECT, most informative failure for anything that tries to load
/// this file for real before B6 has run. Writing a placeholder int (or a
/// sentinel string, which would fail even louder -- and less
/// informatively -- as a JSON type mismatch during deserialization) would
/// trade that precise error for either a silently-wrong threshold or a
/// worse one.
public sealed record RoundTripThresholdsDocument
{
    public required int FormatVersion { get; init; }

    public required int ReferenceFloor { get; init; }

    public required int IndexArtworkCount { get; init; }

    public required string IndexSha256 { get; init; }

    public required DateTimeOffset MeasuredAt { get; init; }

    public required string Notes { get; init; }

    /// `true` on every value this package writes -- see the type's own
    /// `Write` doc comment. A machine-checkable flag, not just prose in
    /// `Notes`, precisely so nothing downstream can mistake this file for
    /// an authoritative win-x64 measurement by skimming past the notes.
    public required bool Provisional { get; init; }

    public required string MeasuredOn { get; init; }

    public required string MeasuredOnDetail { get; init; }

    public required int SampleSize { get; init; }

    public required int Seed { get; init; }

    public required int LandSampleSize { get; init; }

    public required int NonLandSampleSize { get; init; }

    public required double Rank1ArtworkMatchRate { get; init; }

    public required double Rank1ArtworkMatchRateLands { get; init; }

    public required double Rank1ArtworkMatchRateNonLands { get; init; }

    public required int OwnDistanceMin { get; init; }

    public required double OwnDistanceMean { get; init; }

    public required int OwnDistanceMedian { get; init; }

    public required int OwnDistanceMax { get; init; }

    public required int MarginMin { get; init; }

    public required double MarginMean { get; init; }

    public required int MarginMedian { get; init; }

    public required int MarginMax { get; init; }

    /// The ship architecture's own `ArchitectureProvenance.CurrentToken()`
    /// form -- CLAUDE.md "Platform": "Ship `win-x64` binary only." Any
    /// measurement taken on this token is the shipping measurement, not a
    /// placeholder; any measurement taken on a different token (e.g. this
    /// package's own development history on `arm64-darwin`) is provisional
    /// until re-measured here, per CLAUDE.md "The one gate that matters
    /// most" / Real risk #2 (`INTER_AREA` is not bit-exact across
    /// x86-64/ARM64).
    internal const string ShipArchitectureToken = "x64-windows";

    public static RoundTripThresholdsDocument FromStatistics(
        RoundTripGateStatistics stats, string indexSha256, int indexArtworkCount, DateTimeOffset measuredAt) =>
        FromStatistics(
            stats, indexSha256, indexArtworkCount, measuredAt,
            ArchitectureProvenance.CurrentToken(), ArchitectureProvenance.CurrentDetail());

    /// Testable overload: `architectureToken`/`architectureDetail` are
    /// threaded through explicitly (rather than read from
    /// `ArchitectureProvenance` inside this method) so a test can exercise
    /// BOTH the ship-architecture and foreign-architecture branches
    /// deterministically, regardless of which machine actually runs the
    /// test suite -- `RoundTripThresholdsDocumentTests` covers both this
    /// way. `internal` via `LoreFetch.Lab`'s own `InternalsVisibleTo`
    /// grant to `LoreFetch.Tests.StreamB` (`RepoPaths.cs`).
    internal static RoundTripThresholdsDocument FromStatistics(
        RoundTripGateStatistics stats, string indexSha256, int indexArtworkCount, DateTimeOffset measuredAt,
        string architectureToken, string architectureDetail)
    {
        var isShipArchitecture = string.Equals(architectureToken, ShipArchitectureToken, StringComparison.Ordinal);

        return new()
        {
            // Matches `Core/Scanning/ThresholdsFile.SupportedFormatVersion`.
            // `LoreFetch.Lab` DOES reference `LoreFetch.Core` (see its
            // .csproj), but not that type's own constant directly here --
            // `Core/Scanning` is frozen contract surface this package must
            // not add a new compile-time dependency edge onto for a single
            // literal; both types document "schema v1" in prose instead.
            FormatVersion = 1,
            ReferenceFloor = stats.OwnDistanceMax,
            IndexArtworkCount = indexArtworkCount,
            IndexSha256 = indexSha256,
            MeasuredAt = measuredAt,
            Notes = BuildNotes(stats, isShipArchitecture, architectureDetail),
            // Derived from the architecture that actually took this
            // measurement, not hardcoded -- a `round-trip-gate --out` run
            // on win-x64 (the ship architecture) is the FINAL, shipping
            // measurement, not a placeholder pending a later re-measurement
            // on itself. Any other architecture stays provisional, exactly
            // as before.
            Provisional = !isShipArchitecture,
            MeasuredOn = architectureToken,
            MeasuredOnDetail = architectureDetail,
            SampleSize = stats.SampleSize,
            Seed = 0, // overwritten by the caller, which knows the actual seed used
            LandSampleSize = stats.LandSampleSize,
            NonLandSampleSize = stats.NonLandSampleSize,
            Rank1ArtworkMatchRate = stats.Rank1Rate,
            Rank1ArtworkMatchRateLands = stats.LandRank1Rate,
            Rank1ArtworkMatchRateNonLands = stats.NonLandRank1Rate,
            OwnDistanceMin = stats.OwnDistanceMin,
            OwnDistanceMean = stats.OwnDistanceMean,
            OwnDistanceMedian = stats.OwnDistanceMedian,
            OwnDistanceMax = stats.OwnDistanceMax,
            MarginMin = stats.MarginMin,
            MarginMean = stats.MarginMean,
            MarginMedian = stats.MarginMedian,
            MarginMax = stats.MarginMax,
        };
    }

    /// Notes text is factual for WHICHEVER architecture actually produced
    /// this measurement -- no hardcoded "measured on the Mac". A win-x64
    /// (ship architecture) run states plainly that the numbers are final,
    /// not provisional; any other architecture keeps the original
    /// re-measurement warning, now naming the ACTUAL architecture
    /// (`architectureDetail`) rather than assuming it was the Mac.
    private static string BuildNotes(RoundTripGateStatistics stats, bool isShipArchitecture, string architectureDetail)
    {
        var provenance = isShipArchitecture
            ? $"Measured on {architectureDetail}, the win-x64 ship architecture (CLAUDE.md \"Platform\") -- " +
              "referenceFloor and the margin statistics below are the shipping measurement, not provisional."
            : $"PROVISIONAL -- measured on {architectureDetail}, NOT the win-x64 ship architecture. " +
              "INTER_AREA is not bit-exact across x86-64/ARM64 (OpenCV #24163 confirmed, #22477 closed won't-fix; " +
              "CLAUDE.md \"The one gate that matters most\", Real risk #2), and that includes the QUERY side's own " +
              "32x32 resize -- so referenceFloor and the margin statistics below must be RE-MEASURED on win-x64 " +
              "(docs/history/orchestration-plan.md \"Machine split\" rule 4) before being treated as the shipping thresholds.";

        return provenance + " " +
            "goodDistance/okDistance are NOT YET SET -- that is B6's job, calibrated from the real fixture corpus " +
            "(gated on H3); ThresholdsFile.Load will throw \"missing required field\" until B6 adds them, by design. " +
            $"Rank-1 ArtworkId match rate: {stats.Rank1Rate:P1} overall ({stats.CorrectCount}/{stats.AvailableCount}), " +
            $"{stats.LandRank1Rate:P1} on basic lands ({stats.LandCorrectCount}/{stats.LandSampleSize}), " +
            $"{stats.NonLandRank1Rate:P1} on non-lands ({stats.NonLandCorrectCount}/{stats.NonLandSampleSize}).";
    }
}

public static class RoundTripThresholdsWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// Writes `path` atomically -- temp file in the SAME directory (never
    /// the system temp dir: CLAUDE.md's own store-write trap, "%TEMP%
    /// silently degrades the rename to copy+delete"), then
    /// `File.Move(overwrite: true)`. `seed` is threaded through separately
    /// from `RoundTripThresholdsDocument.FromStatistics` because
    /// `RoundTripGateStatistics` itself does not carry the seed that
    /// produced its sample -- only `RoundTripGateSummary` does.
    ///
    /// Read-merge-write, not a blind overwrite: this type deliberately does
    /// NOT declare `goodDistance`/`okDistance` (see this type's own doc
    /// comment -- those are B6's job, calibrated from the real fixture
    /// corpus). B6 writes them into the SAME file, via
    /// `ThresholdsCalibration.Write`'s own read-merge-write. Without this
    /// merge, re-running `round-trip-gate --out data/index/thresholds.json`
    /// after B6 had already calibrated would silently DROP
    /// `goodDistance`/`okDistance` from the committed file -- the freshly
    /// serialized `RoundTripThresholdsDocument` simply has no such
    /// properties -- and the next `ThresholdsFile.Load` would throw
    /// "missing required field" against a file that, moments earlier,
    /// loaded fine. So: read whatever is already at `path` first, then
    /// layer every property THIS document owns on top (always the fresh
    /// measurement -- referenceFloor, indexSha256, margin stats, etc. must
    /// never be stale), while any key already present that this document
    /// does not know about (`goodDistance`, `okDistance`, and any future
    /// B6-only field) passes through untouched. A target that does not
    /// exist yet, or is not valid JSON, is treated as empty -- there is
    /// nothing to preserve in either case, and B2 runs before B6 on a fresh
    /// checkout, so "file does not exist yet" is the common case, not an
    /// error.
    public static void Write(string path, RoundTripThresholdsDocument document, int seed)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(document);

        var withSeed = document with { Seed = seed };
        var freshNode = JsonSerializer.SerializeToNode(withSeed, SerializerOptions)?.AsObject()
            ?? new JsonObject();

        var preserved = ReadExistingObject(path);
        foreach (var property in preserved)
        {
            if (!freshNode.ContainsKey(property.Key))
            {
                // Deep-clone: a JsonNode can only ever be attached to one
                // parent, so the node read from `preserved` must be
                // detached (or cloned) before it can be re-attached here.
                freshNode[property.Key] = property.Value?.DeepClone();
            }
        }

        var json = freshNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
    }

    /// Best-effort read of whatever JSON object currently sits at `path` --
    /// an absent file or unparsable content both mean "nothing to
    /// preserve" rather than an error, since B2 legitimately runs before
    /// any B6 calibration has ever written to this path.
    private static JsonObject ReadExistingObject(string path)
    {
        if (!File.Exists(path))
        {
            return new JsonObject();
        }

        try
        {
            var existingJson = File.ReadAllText(path);
            return JsonNode.Parse(existingJson)?.AsObject() ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }
}
