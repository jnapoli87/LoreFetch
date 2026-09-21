using LoreFetch.Core.Abstractions;

namespace LoreFetch.Core.Fakes;

/// Synthetic oracle catalog, large by default (~33,000 entries) because
/// `AutoCompleteBox`'s only real performance problem shows up only at that
/// size — a catalog of a few hundred entries cannot reproduce it. Always
/// carries the five hostile names the CSV and type-ahead paths are tested
/// against: a comma plus embedded double quotes, a leading double quote, a
/// non-ASCII accent with an apostrophe, commas inside a number, and a
/// leading '+' that Excel treats as a formula.
public sealed class StubOracleCatalog : IOracleCatalog
{
    // Verbatim, character-for-character — these are the CSV/type-ahead edge
    // cases the whole pipeline is tested against.
    private static readonly string[] HostileNames =
    {
        "Kongming, \"Sleeping Dragon\"",
        "\"Rumors of My Death . . .\"",
        "Lim-Dûl's Vault",
        "Borrowing 100,000 Arrows",
        "+2 Mace",
    };

    /// `entryCount` defaults to ~33,000. The five hostile names above are
    /// always present, even when `entryCount` is smaller than five — in that
    /// case the catalog simply has five entries, all hostile, rather than
    /// dropping any of them.
    public StubOracleCatalog(int entryCount = 33_000)
    {
        if (entryCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(entryCount), entryCount, "entryCount must be non-negative.");
        }

        var total = Math.Max(entryCount, HostileNames.Length);
        var entries = new List<OracleEntry>(total);

        for (var i = 0; i < HostileNames.Length; i++)
        {
            entries.Add(new OracleEntry($"hostile-{i}", HostileNames[i]));
        }

        for (var i = HostileNames.Length; i < total; i++)
        {
            entries.Add(new OracleEntry($"synthetic-{i}", $"Synthetic Card {i}"));
        }

        All = entries;
    }

    public IReadOnlyList<OracleEntry> All { get; }
}
