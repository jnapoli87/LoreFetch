using LoreFetch.Lab.Bulk;

namespace LoreFetch.Lab.Printings;

public sealed record PrintingsAnalysisResult(
    int InScopePrintingCount,
    int DistinctIllustrationCount,
    int SingletonIllustrationCount,
    double SingletonFraction);

/// The `printings` command's grouping/fraction logic -- the open question
/// deferred from the `ArtworkId` ruling (2026-09-21, see
/// docs/history/orchestration-plan.md item B4a): what fraction of in-scope
/// artworks (illustration_ids) have exactly one in-scope printing? Where
/// the art is unambiguous, the art match *is* the printing.
///
/// A pure function over already-parsed records, same reasoning as
/// `ArtworkFilterCascade`: unit-testable on a synthetic sample, no
/// network or JSON parsing in view.
public static class PrintingsAnalyzer
{
    public const string FilterDescription =
        "lang == \"en\" && finishes contains \"nonfoil\" && has top-level image_uris " +
        "(single-faced) && frame == \"2015\" && layout/set_type not in the same " +
        "excluded sets as the bulk cascade";

    public static PrintingsAnalysisResult Analyze(IEnumerable<RawPrinting> printings)
    {
        var inScope = printings.Where(IsInScope).ToList();

        // Grouping needs a key. Every printing that reaches this point is
        // single-faced, English, non-foil and 2015-frame, so Scryfall's
        // schema guarantees illustration_id -- the empty-string guard is
        // defensive rather than expected to ever trigger on live data.
        var groups = inScope
            .Where(p => p.IllustrationId.Length > 0)
            .GroupBy(p => p.IllustrationId, StringComparer.Ordinal)
            .ToList();

        var distinctIllustrations = groups.Count;
        var singletons = groups.Count(g => g.Count() == 1);
        var fraction = distinctIllustrations == 0 ? 0.0 : (double)singletons / distinctIllustrations;

        return new PrintingsAnalysisResult(inScope.Count, distinctIllustrations, singletons, fraction);
    }

    private static bool IsInScope(RawPrinting p) =>
        p.Lang == "en"
        && p.Finishes.Contains("nonfoil", StringComparer.Ordinal)
        && p.HasImageUris
        && p.Frame == "2015"
        && !ScryfallScopeFilters.ExcludedLayouts.Contains(p.Layout)
        && !ScryfallScopeFilters.ExcludedSetTypes.Contains(p.SetType);
}
