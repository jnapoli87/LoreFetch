using System.Text.Json;

namespace LoreFetch.Lab.Bulk;

/// The fields of one `unique_artwork` bulk-data object that the filter
/// cascade and manifest need. Deliberately not the raw `JsonElement` --
/// the cascade in `ArtworkFilterCascade` is a plain function over this
/// type so it can be unit-tested without any JSON machinery in view.
public sealed record RawArtwork(
    string Id,             // Scryfall card id -- this IS the ArtworkId (see CONTRACTS.md)
    string OracleId,
    string Name,
    string TypeLine,
    string Lang,
    string ImageStatus,
    string Layout,
    string SetType,
    string Frame,
    string? ImageUriNormal) // null when the object has no top-level `image_uris`
                            // at all -- the multi-faced case this project must
                            // skip explicitly rather than crash or drop silently.
{
    public static RawArtwork Parse(JsonElement e) => new(
        Id: GetString(e, "id"),
        OracleId: GetString(e, "oracle_id"),
        Name: GetString(e, "name"),
        TypeLine: GetString(e, "type_line"),
        Lang: GetString(e, "lang"),
        ImageStatus: GetString(e, "image_status"),
        Layout: GetString(e, "layout"),
        SetType: GetString(e, "set_type"),
        Frame: GetString(e, "frame"),
        ImageUriNormal: GetNestedStringOrNull(e, "image_uris", "normal"));

    /// Missing or non-string fields become "" rather than throwing --
    /// a maintainer tool over a live, evolving external feed should
    /// report an honest (if degraded) count rather than crash on the
    /// one object the schema didn't quite predict.
    internal static string GetString(JsonElement e, string property) =>
        e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : "";

    internal static string? GetNestedStringOrNull(JsonElement e, string outerProperty, string innerProperty) =>
        e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(outerProperty, out var outer)
        && outer.ValueKind == JsonValueKind.Object
        && outer.TryGetProperty(innerProperty, out var inner)
        && inner.ValueKind == JsonValueKind.String
            ? inner.GetString()
            : null;
}
