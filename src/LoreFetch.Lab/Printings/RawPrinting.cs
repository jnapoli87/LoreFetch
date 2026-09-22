using System.Text.Json;
using LoreFetch.Lab.Bulk;

namespace LoreFetch.Lab.Printings;

/// The fields of one `default_cards` bulk-data object that the `printings`
/// command's in-scope filter and illustration grouping need.
public sealed record RawPrinting(
    string IllustrationId,
    string Lang,
    IReadOnlyList<string> Finishes,
    bool HasImageUris, // top-level image_uris object present -- i.e. single-faced
    string Frame,
    string Layout,
    string SetType)
{
    public static RawPrinting Parse(JsonElement e) => new(
        IllustrationId: RawArtwork.GetString(e, "illustration_id"),
        Lang: RawArtwork.GetString(e, "lang"),
        Finishes: GetStringArray(e, "finishes"),
        HasImageUris: e.ValueKind == JsonValueKind.Object
            && e.TryGetProperty("image_uris", out var imageUris)
            && imageUris.ValueKind == JsonValueKind.Object,
        Frame: RawArtwork.GetString(e, "frame"),
        Layout: RawArtwork.GetString(e, "layout"),
        SetType: RawArtwork.GetString(e, "set_type"));

    private static IReadOnlyList<string> GetStringArray(JsonElement e, string property)
    {
        if (e.ValueKind != JsonValueKind.Object
            || !e.TryGetProperty(property, out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>(array.GetArrayLength());
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                result.Add(item.GetString()!);
            }
        }

        return result;
    }
}
