using LoreFetch.Lab.Bulk;

namespace LoreFetch.Lab.Index;

/// Decides WHICH manifest entries a `build-index` run hashes, and in what
/// order -- the one place `BuildIndexCommand`'s three modes (full,
/// `--subset N`, `--ids <file>` with an optional `--fill N`) share, so all
/// three come back in manifest order regardless of which one chose the
/// entries. `IndexBuilder` only guarantees to preserve whatever order it is
/// handed; this type is what decides that order is manifest order in the
/// first place.
public static class IndexSelection
{
    /// The first `count` manifest entries, in manifest order. "First N"
    /// rather than "every Kth": it needs no knowledge of the manifest's
    /// total size to compute, and manifest order is already a stable,
    /// meaningful ordering (Scryfall's own `unique_artwork` order, run
    /// through the filter cascade) rather than an arbitrary one that
    /// striding would somehow make more representative -- a labelled
    /// subset's whole point is a smaller but equally reproducible index,
    /// not a statistically balanced sample.
    public static IReadOnlyList<ManifestEntry> Subset(IReadOnlyList<ManifestEntry> manifest, int count)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "count must be non-negative.");
        }

        return manifest.Take(count).ToList();
    }

    /// The entries named in `ids` (returned in MANIFEST order, not file
    /// order -- see `IndexBuilder`'s determinism note), plus -- when `fill`
    /// is positive -- the first `fill` manifest entries not already
    /// selected, also in manifest order. That makes "four named cards plus
    /// a few more to pad the index out" deterministic and reproducible,
    /// rather than padded with whatever a naive implementation happened to
    /// find first.
    ///
    /// `unmatchedIds` carries any id from `ids` that is not in `manifest`
    /// at all, in the order `ids` listed them, with duplicates removed --
    /// the caller decides whether that is fatal (`BuildIndexCommand` treats
    /// it as one, since a smoke check silently missing one of its named
    /// cards is worse than refusing to build).
    public static IReadOnlyList<ManifestEntry> ByIds(
        IReadOnlyList<ManifestEntry> manifest,
        IReadOnlyList<string> ids,
        int fill,
        out IReadOnlyList<string> unmatchedIds)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(ids);
        if (fill < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fill), fill, "fill must be non-negative.");
        }

        var manifestIds = new HashSet<string>(manifest.Select(e => e.ArtworkId), StringComparer.Ordinal);
        unmatchedIds = ids
            .Where(id => !manifestIds.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var selected = new HashSet<string>(ids, StringComparer.Ordinal);

        if (fill > 0)
        {
            var added = 0;
            foreach (var entry in manifest)
            {
                if (added >= fill)
                {
                    break;
                }

                // HashSet<T>.Add returns true only when the id was not
                // already present -- so this only counts entries as "fill"
                // that the caller's own `ids` list did not already name.
                if (selected.Add(entry.ArtworkId))
                {
                    added++;
                }
            }
        }

        return manifest.Where(e => selected.Contains(e.ArtworkId)).ToList();
    }
}
