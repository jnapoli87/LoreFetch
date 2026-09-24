namespace LoreFetch.Lab.Bulk;

/// One step of the cascade: its name (printed to the console) and the
/// art/oracle-id counts that survived it.
public sealed record CascadeStepResult(string Name, int ArtCount, int DistinctOracleIdCount);

public sealed record ArtworkCascadeResult(
    IReadOnlyList<CascadeStepResult> Steps,
    int SkippedNoImageUrisCount,
    IReadOnlyList<RawArtwork> Survivors,
    int InformationalFrame2015Count)
{
    public CascadeStepResult FinalStep => Steps[^1];
}

/// The `bulk` command's filter cascade, in the exact order fixed by
/// `CLAUDE.md`/`docs/design/identification.md` §B4. A pure function over
/// already-parsed records, deliberately -- so this can be unit-tested
/// against a small committed JSONL sample without any network access, and
/// so `Program`'s console printing is the only part of `bulk` that isn't
/// covered by a fast, offline test.
public static class ArtworkFilterCascade
{
    public const string RawStepName = "raw unique_artwork";
    public const string ImageStatusStepName = "image_status ok";
    public const string LangStepName = "lang == \"en\"";
    public const string HasImageUrisStepName = "has image_uris.normal";
    public const string LayoutStepName = "drop excluded layouts";
    public const string SetTypeStepName = "drop excluded set_types";
    public const string DigitalOnlyStepName = "drop digital-only (Alchemy/Arena/MTGO) cards";

    public static ArtworkCascadeResult Run(IEnumerable<RawArtwork> records)
    {
        var all = ToList(records);
        var steps = new List<CascadeStepResult>(7) { Measure(RawStepName, all) };

        var afterImageStatus = all
            .Where(a => a.ImageStatus != "placeholder" && a.ImageStatus != "missing")
            .ToList();
        steps.Add(Measure(ImageStatusStepName, afterImageStatus));

        var afterLang = afterImageStatus.Where(a => a.Lang == "en").ToList();
        steps.Add(Measure(LangStepName, afterLang));

        // The explicit skip this step exists for: 3,440 live objects have
        // no top-level `image_uris` at all (multi-faced -- images live per
        // face). Counted here rather than silently disappearing into "some
        // arts didn't make it."
        var skippedNoImageUris = afterLang.Count(a => a.ImageUriNormal is null);
        var afterImageUris = afterLang.Where(a => a.ImageUriNormal is not null).ToList();
        steps.Add(Measure(HasImageUrisStepName, afterImageUris));

        var afterLayout = afterImageUris
            .Where(a => !ScryfallScopeFilters.ExcludedLayouts.Contains(a.Layout))
            .ToList();
        steps.Add(Measure(LayoutStepName, afterLayout));

        var afterSetType = afterLayout
            .Where(a => !ScryfallScopeFilters.ExcludedSetTypes.Contains(a.SetType))
            .ToList();
        steps.Add(Measure(SetTypeStepName, afterSetType));

        // Digital-only objects (Alchemy rebalances, and any other
        // Arena-/MTGO-only printing) share artwork with their paper twin
        // (or, for an Arena-original card, simply can never appear on a
        // physical table) -- either way the hash can never legitimately
        // retrieve one for a scanned card, only produce a confident wrong
        // answer (CLAUDE.md Risk 5). `Digital` is Scryfall's own flag for
        // this, not a name-prefix heuristic -- see RawArtwork.Digital.
        var afterDigital = afterSetType.Where(a => !a.Digital).ToList();
        steps.Add(Measure(DigitalOnlyStepName, afterDigital));

        // Informational only -- NOT applied. Reported so a reader can see
        // what the stated "modern frame" scope would additionally cost,
        // without the cascade actually dropping those arts.
        var frame2015Count = afterDigital.Count(a => a.Frame == "2015");

        return new ArtworkCascadeResult(steps, skippedNoImageUris, afterDigital, frame2015Count);
    }

    private static CascadeStepResult Measure(string name, IReadOnlyList<RawArtwork> items) => new(
        name,
        items.Count,
        items.Select(a => a.OracleId).Distinct(StringComparer.Ordinal).Count());

    private static IReadOnlyList<RawArtwork> ToList(IEnumerable<RawArtwork> records) =>
        records as IReadOnlyList<RawArtwork> ?? records.ToList();
}
