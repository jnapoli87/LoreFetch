namespace LoreFetch.App.Fakes;

/// <summary>
/// The two distances <see cref="AppComposition"/> assigns to
/// <see cref="LoreFetch.Core.Abstractions.ScanSettings.GoodDistance"/> and
/// <see cref="LoreFetch.Core.Abstractions.ScanSettings.OkDistance"/> in
/// <c>CompositionMode.Fakes</c> — and, by design, the <b>only</b> place in
/// <c>LoreFetch.App</c> that names a distance literal.
/// </summary>
/// <remarks>
/// <para>
/// These are demo values for the fake identifier, <b>not measured</b>.
/// <see cref="LoreFetch.Core.Scanning.ThresholdsFile"/> is stream B's real
/// schema, loaded from stream B's own measured
/// <c>data/index/thresholds.json</c> — but <c>LoreFetch.App.csproj</c> is
/// frozen and ships only <c>data/index/**</c> (that file does not exist yet;
/// stream B produces it), and there is no Avalonia/MSBuild default glob that
/// would let this stream ship a second JSON resource without an edit to that
/// frozen project file. So Fakes mode — the only mode this stream builds —
/// cannot load a real thresholds file, and without SOME non-zero good/ok
/// pair every hash tile is <c>Unresolved</c> (both default to 0 on a fresh
/// <c>ScanSettings</c>), which makes the keyboard loop unable to commit
/// anything. This class is the sanctioned exception: one file, named for
/// exactly what it is, so nothing else in the App ever needs its own
/// distance constant.
/// </para>
/// <para>
/// <b>Real</b> mode (integration item I2, added once streams B/C/D land)
/// loads the genuine values via
/// <see cref="LoreFetch.Core.Scanning.ThresholdsFile.Load"/> from
/// <see cref="LoreFetch.Core.Scanning.DataFiles.ThresholdsPath"/> instead of
/// this class — <c>DemoThresholds</c> is never consulted outside
/// <c>CompositionMode.Fakes</c>.
/// </para>
/// <para>
/// Everything downstream of <see cref="LoreFetch.Core.Abstractions.ScanSettings"/>
/// — including the demo identifier that drives the cohort grid through all
/// its reachable states — derives its distances from
/// <c>GoodDistance</c>/<c>OkDistance</c> at runtime rather than holding its
/// own copy of these numbers, so this stays the one place they are written
/// down.
/// </para>
/// </remarks>
public static class DemoThresholds
{
    /// <summary>
    /// At or below this Hamming distance, <c>CohortTile</c> proposes a
    /// confident (non-low-confidence) match.
    /// </summary>
    public const int GoodDistance = 100;

    /// <summary>
    /// Above <see cref="GoodDistance"/> and at or below this distance,
    /// <c>CohortTile</c> still proposes a match, but flags it
    /// <c>IsLowConfidence</c>. Above this distance the tile is
    /// <c>Unresolved</c>.
    /// </summary>
    public const int OkDistance = 200;
}
