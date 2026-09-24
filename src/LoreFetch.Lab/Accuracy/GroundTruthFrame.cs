using LoreFetch.Core.Identification;

namespace LoreFetch.Lab.Accuracy;

/// A `GroundTruthRow` enriched with what the loaded hash index says about
/// its `OracleName` -- resolved ONCE, up front, rather than re-looked-up per
/// classification. Keeping `ExpectedOracleId` (rather than comparing names)
/// matches DECISIONS.md's own identity rule ("OracleId is the identity key
/// everywhere in LoreFetch... never OracleName") and lets
/// `AccuracyFrameRunner` compare against `CardCandidate.OracleId` directly.
public sealed record ResolvedGroundTruthRow(GroundTruthRow Row, string ExpectedOracleId, bool IsBasicLand);

/// Resolves every `GroundTruthRow.OracleName` against the loaded index's own
/// oracle table -- the SAME lookup the round-trip gate and the crop-scale
/// experiment already build the equivalent of (`RoundTripSampler`,
/// `CropScaleExperimentRunner`), except keyed by name instead of by
/// `HashIndexEntry` because a ground-truth row only ever names a card by its
/// canonical oracle name (`capture-fixtures.sh`'s own `resolve_cards`
/// rewrites `--card` values to the catalog's canonical spelling before they
/// ever reach the CSV).
///
/// Deliberately throws, rather than skipping, when a name is absent: an
/// operator-entered name the loaded index does not recognise is a setup
/// mismatch between the corpus and the index, not a "no-match" identification
/// outcome -- conflating the two would silently hide a broken run behind a
/// bad accuracy number.
public static class GroundTruthOracleLookup
{
    public static IReadOnlyDictionary<string, (string OracleId, bool IsBasicLand)> BuildNameIndex(HashIndexData index)
    {
        ArgumentNullException.ThrowIfNull(index);

        // IsBasicLand lives on HashIndexEntry (per-artwork), not on
        // HashIndexData.OracleTable's deduplicated (OracleId, OracleName)
        // pairs -- V11/B4d's own flag is per-entry, but is invariant across
        // every artwork of the same oracle card (a basic land oracle card
        // has no non-land printings), so the first entry seen for an
        // OracleId settles it for every row that names that card.
        var byName = new Dictionary<string, (string OracleId, bool IsBasicLand)>(StringComparer.Ordinal);
        foreach (var entry in index.Entries)
        {
            if (!byName.ContainsKey(entry.OracleName))
            {
                byName[entry.OracleName] = (entry.OracleId, entry.IsBasicLand);
            }
        }

        return byName;
    }

    /// Throws `InvalidOperationException` naming every unresolved
    /// `oracle_name` at once (not just the first) -- a corpus/index mismatch
    /// is a setup problem worth fixing in one pass, not one crash at a time.
    public static IReadOnlyList<ResolvedGroundTruthRow> Resolve(
        HashIndexData index, IReadOnlyList<GroundTruthRow> rows)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(rows);

        var byName = BuildNameIndex(index);
        var resolved = new List<ResolvedGroundTruthRow>(rows.Count);
        var unresolved = new List<string>();

        foreach (var row in rows)
        {
            if (byName.TryGetValue(row.OracleName, out var found))
            {
                resolved.Add(new ResolvedGroundTruthRow(row, found.OracleId, found.IsBasicLand));
            }
            else
            {
                unresolved.Add($"{row.File}:slot{row.Slot} (\"{row.OracleName}\")");
            }
        }

        if (unresolved.Count > 0)
        {
            throw new InvalidOperationException(
                $"ground-truth.csv names {unresolved.Count} oracle_name value(s) the loaded index does not " +
                $"recognise -- a corpus/index mismatch, not an identification outcome: {string.Join("; ", unresolved.Take(10))}" +
                (unresolved.Count > 10 ? $" (+{unresolved.Count - 10} more)" : string.Empty));
        }

        return resolved;
    }
}

/// One captured fixture: its own file path, height, layout, and every slot
/// the operator recorded for it, ordered ascending by `Slot` -- validated to
/// be exactly `1..Layout`, no gaps and no duplicates, since that is what
/// makes "row-major slot order" well-defined at all.
public sealed record GroundTruthFrame(string File, double HeightIn, int Layout, IReadOnlyList<ResolvedGroundTruthRow> Slots)
{
    /// Groups already-resolved rows by `File` and validates each group's
    /// slot numbering. Throws `GroundTruthCsvFormatException` on a
    /// malformed group (mixed height/layout under one file, or a slot set
    /// that isn't exactly `1..Layout`) -- this is a corpus integrity check,
    /// not a per-card identification outcome, so it fails loudly rather
    /// than silently dropping the frame.
    public static IReadOnlyList<GroundTruthFrame> GroupByFile(IReadOnlyList<ResolvedGroundTruthRow> resolvedRows)
    {
        ArgumentNullException.ThrowIfNull(resolvedRows);

        var frames = new List<GroundTruthFrame>();
        foreach (var group in resolvedRows.GroupBy(r => r.Row.File, StringComparer.Ordinal))
        {
            var members = group.OrderBy(r => r.Row.Slot).ToList();
            var first = members[0].Row;

            foreach (var member in members)
            {
                if (member.Row.HeightIn != first.HeightIn || member.Row.Layout != first.Layout)
                {
                    throw new GroundTruthCsvFormatException(
                        $"ground-truth.csv: file '{first.File}' has rows disagreeing on height_in/layout " +
                        $"(slot {first.Slot}: {first.HeightIn}in/L{first.Layout} vs slot {member.Row.Slot}: " +
                        $"{member.Row.HeightIn}in/L{member.Row.Layout}).");
                }
            }

            var expectedSlots = Enumerable.Range(1, first.Layout).ToList();
            var actualSlots = members.Select(m => m.Row.Slot).ToList();
            if (!expectedSlots.SequenceEqual(actualSlots))
            {
                throw new GroundTruthCsvFormatException(
                    $"ground-truth.csv: file '{first.File}' declares layout {first.Layout} but its slot numbers " +
                    $"are [{string.Join(",", actualSlots)}], not exactly 1..{first.Layout}.");
            }

            frames.Add(new GroundTruthFrame(first.File, first.HeightIn, first.Layout, members));
        }

        return frames;
    }
}
