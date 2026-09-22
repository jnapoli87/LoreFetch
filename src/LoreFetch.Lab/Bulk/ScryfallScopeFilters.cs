namespace LoreFetch.Lab.Bulk;

/// The layout/set_type exclusions shared between the `bulk` cascade (over
/// `unique_artwork`) and the `printings` in-scope filter (over
/// `default_cards`) -- both are "things that are not cards" in the sense
/// this project cares about, and keeping one copy means the two commands
/// can never quietly disagree about what a token or a memorabilia object
/// is.
public static class ScryfallScopeFilters
{
    /// Layouts that are not single-faced playable cards: tokens, the
    /// artwork-only `art_series` layout, and non-card layouts (emblem,
    /// scheme, planar, vanguard). Double-faced tokens are listed
    /// separately from `token` because Scryfall gives them their own
    /// layout name.
    public static readonly IReadOnlySet<string> ExcludedLayouts = new HashSet<string>(StringComparer.Ordinal)
    {
        "token",
        "double_faced_token",
        "art_series",
        "emblem",
        "scheme",
        "planar",
        "vanguard",
    };

    /// Set types that hold non-card or promotional objects rather than
    /// cards someone would scan off a table.
    public static readonly IReadOnlySet<string> ExcludedSetTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "token",
        "memorabilia",
        "art_series",
        "minigame",
    };
}
