using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Identification;
using LoreFetch.Lab.CropScale;
using OpenCvSharp;
using Xunit;

namespace LoreFetch.Tests.Lab.CropScale;

/// A fully SYNTHETIC smoke test for `CropScaleExperimentRunner` -- unlike
/// `CropScaleCommand`'s real-cache run (measured by hand for
/// `docs/accuracy.md`), this never needs the external 5 GB Scryfall cache,
/// so it runs in CI on every machine. It builds its own tiny index and
/// cache directory (temp, never in the repo), computing each entry's
/// reference hash via `ReferenceTransform` exactly as a real `build-index`
/// run would -- so this is a genuine (if small) exercise of the same
/// reference/query asymmetry the whole project depends on, not a
/// hand-picked fixture.
public class CropScaleExperimentRunnerTests
{
    private readonly ITestOutputHelper _output;

    public CropScaleExperimentRunnerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Run_SmallSyntheticIndex_ZeroInsetIsHighRank1AndLargeInsetDegrades()
    {
        var tempCacheDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"lorefetch-cropscale-test-{Guid.NewGuid():N}"));
        try
        {
            var (index, identifier) = BuildSyntheticIndexAndCache(tempCacheDir.FullName, artworkCount: 12);

            var options = new CropScaleExperimentOptions
            {
                CacheDir = tempCacheDir.FullName,
                SampleSize = 12,
                Seed = 20260922,
                Levels =
                [
                    new CropScaleLevel(0f, 0f, "0.00"),
                    new CropScaleLevel(0.15f, 0.15f, "0.15"),
                ],
            };

            var summary = CropScaleExperimentRunner.Run(index, identifier, options);

            Assert.Equal(2, summary.Points.Count);
            var zeroInset = summary.Points.Single(p => p.WidthInsetFraction == 0f);
            var largeInset = summary.Points.Single(p => p.WidthInsetFraction == 0.15f);
            _output.WriteLine(
                $"zero: rank1={zeroInset.Statistics.Rank1Rate:P1}, ownMean={zeroInset.Statistics.OwnDistanceMean:F1}, correct={zeroInset.Statistics.CorrectCount}/{zeroInset.Statistics.AvailableCount}\n" +
                $"large: rank1={largeInset.Statistics.Rank1Rate:P1}, ownMean={largeInset.Statistics.OwnDistanceMean:F1}, correct={largeInset.Statistics.CorrectCount}/{largeInset.Statistics.AvailableCount}");

            // Zero inset is a render-to-self match through the shipping
            // query path -- CLAUDE.md's own gate expects a small, stable
            // distance and a 100% rank-1 rate for exactly this population
            // shape (B2's own measurement). A 15% inset is well outside the
            // observed real-world range and must degrade it substantially.
            Assert.Equal(1.0, zeroInset.Statistics.Rank1Rate);
            Assert.True(
                largeInset.Statistics.Rank1Rate < zeroInset.Statistics.Rank1Rate,
                $"A 15% inset should degrade rank-1 rate below the zero-inset baseline: " +
                $"{zeroInset.Statistics.Rank1Rate:P1} -> {largeInset.Statistics.Rank1Rate:P1}.");

            // OwnDistanceMean is computed only over CORRECT outcomes
            // (RoundTripGateStatistics.From), so a level with zero correct
            // matches reports -1 rather than a comparable mean -- that is
            // itself even stronger evidence of degradation than a raised
            // mean would be, so both outcomes count as "degraded."
            Assert.True(
                largeInset.Statistics.CorrectCount == 0 || largeInset.Statistics.OwnDistanceMean > zeroInset.Statistics.OwnDistanceMean,
                "A 15% inset should raise the mean own-distance above the zero-inset baseline (or collapse rank-1 to zero correct matches).");
        }
        finally
        {
            Directory.Delete(tempCacheDir.FullName, recursive: true);
        }
    }

    [Fact]
    public void Run_LandOnlyIndex_ExcludesLandsAndSamplesNothing()
    {
        var tempCacheDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"lorefetch-cropscale-test-{Guid.NewGuid():N}"));
        try
        {
            var (index, identifier) = BuildSyntheticIndexAndCache(tempCacheDir.FullName, artworkCount: 4, allBasicLands: true);

            var options = new CropScaleExperimentOptions
            {
                CacheDir = tempCacheDir.FullName,
                SampleSize = 10, // clamps to however many non-lands exist -- here, zero
                Seed = 1,
                Levels = [new CropScaleLevel(0f, 0f, "0.00")],
            };

            var summary = CropScaleExperimentRunner.Run(index, identifier, options);

            // CLAUDE.md's Ladder: "Measure accuracy on normal cards only" --
            // an all-land index must sample and measure NOTHING, not fall
            // back to lands, confirming the non-land filter is a hard
            // exclusion rather than a soft preference.
            Assert.Equal(0, summary.SampleSize);
            Assert.Equal(0, summary.Points.Single().Statistics.SampleSize);
        }
        finally
        {
            Directory.Delete(tempCacheDir.FullName, recursive: true);
        }
    }

    [Fact]
    public void Run_MissingCacheImage_ThrowsRatherThanSilentlySkipping()
    {
        var tempCacheDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"lorefetch-cropscale-test-{Guid.NewGuid():N}"));
        try
        {
            var (index, identifier) = BuildSyntheticIndexAndCache(tempCacheDir.FullName, artworkCount: 3);

            // Delete one cached image after the index was built against it --
            // simulates an incomplete cache, which this runner (unlike the
            // round-trip gate, which tolerates gaps) must refuse to measure
            // through silently.
            var firstArtworkPath = Path.Combine(tempCacheDir.FullName, $"{index.Entries[0].ArtworkId}.jpg");
            File.Delete(firstArtworkPath);

            var options = new CropScaleExperimentOptions
            {
                CacheDir = tempCacheDir.FullName,
                SampleSize = 3,
                Seed = 1,
                Levels = [new CropScaleLevel(0f, 0f, "0.00")],
            };

            Assert.Throws<InvalidOperationException>(() => CropScaleExperimentRunner.Run(index, identifier, options));
        }
        finally
        {
            Directory.Delete(tempCacheDir.FullName, recursive: true);
        }
    }

    private static (HashIndexData Index, HashCardIdentifier Identifier) BuildSyntheticIndexAndCache(
        string cacheDir, int artworkCount, bool allBasicLands = false)
    {
        var entries = new List<HashIndexEntry>(artworkCount);
        for (var i = 0; i < artworkCount; i++)
        {
            using var color = SyntheticImages.MakeCardLikeBgr(RectifiedCard.CanonicalWidth, RectifiedCard.CanonicalHeight, seed: 5000 + i);

            // The base gradient alone is nearly hash-indistinguishable
            // between seeds once ReferenceTransform's blur+downsample
            // washes out the per-pixel noise that is the only thing
            // varying between them -- a real Scryfall render has genuine
            // macro-scale art/title differences a synthetic gradient does
            // not. A large, per-index-positioned marker inside the top
            // 61% (CardHasher's own region crop) survives that
            // blur+downsample and gives this small synthetic set the
            // between-card separation a real index has for free.
            var markerX = 40 + ((i * (RectifiedCard.CanonicalWidth - 80)) / Math.Max(1, artworkCount - 1));
            Cv2.Circle(color, new Point(markerX, 100), radius: 45, new Scalar(0, 0, 0), thickness: -1);

            var artworkId = $"synthetic-art-{i:D3}";
            Cv2.ImWrite(Path.Combine(cacheDir, $"{artworkId}.jpg"), color);

            using var referenceGray = ReferenceTransform.Prepare(color);
            var hash = CardHasher.Hash(referenceGray);

            entries.Add(new HashIndexEntry(hash, OracleId: $"oracle-{i:D3}", OracleName: $"Synthetic {i}", artworkId, IsBasicLand: allBasicLands));
        }

        var oracleTable = entries.Select(e => new OracleEntry(e.OracleId, e.OracleName)).ToList();
        var index = new HashIndexData(entries, oracleTable);
        var identifier = new HashCardIdentifier(index);
        return (index, identifier);
    }
}
