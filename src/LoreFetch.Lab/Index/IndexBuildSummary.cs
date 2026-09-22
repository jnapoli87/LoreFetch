using System.Text.Json;

namespace LoreFetch.Lab.Index;

/// The `<out>.build.json` sidecar `build-index` writes next to the index
/// file itself -- everything B4d (the orchestrator's real committed-index
/// run) copies into `thresholds.json` and the plan checklist (`artworkCount`,
/// `sha256`), plus enough provenance (which manifest, which subset/ids mode)
/// that a later reader does not have to re-derive it from the console log.
public sealed record IndexBuildSummary(
    int ArtworkCount,
    int OracleCount,
    int BasicLandCount,
    long FileSizeBytes,
    string Sha256,
    double ElapsedSeconds,
    double ThroughputPerSecond,
    string ManifestPath,
    string ManifestSha256,
    int ManifestEntryCount,
    int SelectedCount,
    string Mode,
    bool AllowMissing,
    int MissingCount,
    int UnexpectedSizeCount,
    DateTime BuiltAtUtc);

public static class IndexBuildSummaryJson
{
    /// camelCase so the field names match exactly what the orchestrator's
    /// brief names verbatim -- "artworkCount", "sha256" -- rather than this
    /// type's own PascalCase C# property names.
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
}
