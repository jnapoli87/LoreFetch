using System.Text.Json;
using System.Text.Json.Serialization;

namespace LoreFetch.Core.Scanning;

/// Stream B's measured confidence thresholds, committed next to the hash
/// index (`data/index/thresholds.json`) and loaded once at startup into
/// `ScanSettings` (CONTRACTS.md "Settings" — "Stream 0 also loads stream B's
/// thresholds file into `ScanSettings`, so stream A never implements a file
/// format that stream B defines"). Schema v1 only.
///
/// Loading is deliberately loud rather than lenient: a silently-defaulted
/// threshold is a silently-wrong confidence gate, so a missing file, an
/// unsupported `formatVersion`, a missing/`null` field or malformed JSON all
/// throw rather than fall back to a placeholder.
public sealed class ThresholdsFile
{
    public const int SupportedFormatVersion = 1;

    public required int FormatVersion { get; init; }

    public required int GoodDistance { get; init; }

    public required int OkDistance { get; init; }

    public required int ReferenceFloor { get; init; }

    public required int IndexArtworkCount { get; init; }

    public required string IndexSha256 { get; init; }

    public required DateTimeOffset MeasuredAt { get; init; }

    /// The one field allowed to be an empty string — but it must still be
    /// PRESENT. A missing `notes` key and a present-but-empty one are
    /// different things: only the latter loads.
    public required string Notes { get; init; }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        // The file is camelCase, the type is PascalCase — .NET's default
        // property matching is case-SENSITIVE (PropertyNameCaseInsensitive
        // defaults to false), so without this every field would silently
        // read back as absent rather than mismatched.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static ThresholdsFile Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Thresholds file not found: {path}", path);
        }

        var json = File.ReadAllText(path);

        Dto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<Dto>(json, SerializerOptions);
        }
        catch (JsonException ex)
        {
            // Wrapped so callers have exactly one exception type to handle
            // for "this file is not usable" — a raw JsonException must never
            // escape this method.
            throw new InvalidDataException($"Thresholds file is not valid JSON: {path}", ex);
        }

        if (dto is null)
        {
            throw new InvalidDataException($"Thresholds file is empty or JSON 'null': {path}");
        }

        // formatVersion first: an unrecognized version invalidates what
        // every other field even means.
        var formatVersion = RequireField(dto.FormatVersion, "formatVersion");
        if (formatVersion != SupportedFormatVersion)
        {
            throw new InvalidDataException(
                $"Thresholds file has unsupported formatVersion {formatVersion}; " +
                $"this build only understands {SupportedFormatVersion}.");
        }

        return new ThresholdsFile
        {
            FormatVersion = formatVersion,
            GoodDistance = RequireField(dto.GoodDistance, "goodDistance"),
            OkDistance = RequireField(dto.OkDistance, "okDistance"),
            ReferenceFloor = RequireField(dto.ReferenceFloor, "referenceFloor"),
            IndexArtworkCount = RequireField(dto.IndexArtworkCount, "indexArtworkCount"),
            IndexSha256 = RequireField(dto.IndexSha256, "indexSha256"),
            MeasuredAt = RequireField(dto.MeasuredAt, "measuredAt"),

            // Presence-only: an empty string is a legitimate value for
            // notes, so this must NOT use string.IsNullOrEmpty.
            Notes = RequireField(dto.Notes, "notes"),
        };
    }

    /// A `T?` (value-type) DTO property that is `null` covers BOTH an absent
    /// JSON key and a key present with a JSON `null` value — System.Text.Json
    /// deserializes either one to a null nullable-value-type property, and a
    /// non-nullable `int`/`DateTimeOffset` property would instead silently
    /// keep its default (0 / default DateTimeOffset) for a missing key,
    /// which is exactly how a missing field slips past a null check. Using
    /// `T?` here is what makes both cases visible and throw.
    private static T RequireField<T>(T? value, string fieldName)
        where T : struct
    {
        if (value is null)
        {
            throw new InvalidDataException($"Thresholds file is missing required field '{fieldName}'.");
        }

        return value.Value;
    }

    /// String overload of the same rule: missing key and explicit JSON
    /// `null` both deserialize a `string?` DTO property to null, and both
    /// must throw. An empty string is a distinct, present value and passes.
    private static string RequireField(string? value, string fieldName)
    {
        if (value is null)
        {
            throw new InvalidDataException($"Thresholds file is missing required field '{fieldName}'.");
        }

        return value;
    }

    /// Every property is nullable, deliberately: a non-nullable value-type
    /// property would default to 0/default on a missing key instead of
    /// surfacing as "missing", which is the loophole this whole type exists
    /// to close.
    private sealed class Dto
    {
        [JsonPropertyName("formatVersion")]
        public int? FormatVersion { get; set; }

        [JsonPropertyName("goodDistance")]
        public int? GoodDistance { get; set; }

        [JsonPropertyName("okDistance")]
        public int? OkDistance { get; set; }

        [JsonPropertyName("referenceFloor")]
        public int? ReferenceFloor { get; set; }

        [JsonPropertyName("indexArtworkCount")]
        public int? IndexArtworkCount { get; set; }

        [JsonPropertyName("indexSha256")]
        public string? IndexSha256 { get; set; }

        [JsonPropertyName("measuredAt")]
        public DateTimeOffset? MeasuredAt { get; set; }

        [JsonPropertyName("notes")]
        public string? Notes { get; set; }
    }
}
