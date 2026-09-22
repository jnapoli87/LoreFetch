using LoreFetch.Core.Identification;
using LoreFetch.Core.Imaging;
using LoreFetch.Lab.Images;
using LoreFetch.Lab.RoundTrip;
using OpenCvSharp;

namespace LoreFetch.Lab.CropScale;

/// One point on package B5c's crop-scale curve: the width/height inset
/// fractions `CropScaleTransform` was asked to apply (see its own doc
/// comment for the sign convention), and the SAME `RoundTripGateStatistics`
/// aggregation B2 already uses over the resulting outcomes -- so a curve
/// point and a round-trip-gate run report identical fields for identical
/// reasons.
public sealed record CropScalePoint(
    float WidthInsetFraction, float HeightInsetFraction, string Label, RoundTripGateStatistics Statistics);

public sealed record CropScaleExperimentSummary(IReadOnlyList<CropScalePoint> Points, int Seed, int SampleSize);

public sealed record CropScaleLevel(float WidthInsetFraction, float HeightInsetFraction, string Label);

public sealed class CropScaleExperimentOptions
{
    public required string CacheDir { get; init; }

    /// Non-land artworks only (CLAUDE.md's "Ladder": "Measure accuracy on
    /// normal cards only" -- every basic land collapses to the same name,
    /// so it would inflate rank-1 without saying anything about crop
    /// sensitivity).
    public int SampleSize { get; init; } = 150;

    public int Seed { get; init; } = 20260922;

    public int MaxCandidates { get; init; } = 3;

    public IReadOnlyList<CropScaleLevel> Levels { get; init; } = DefaultLevels;

    /// Brackets the observed 3-5% defect on both sides, plus one explicit
    /// ANISOTROPIC point at B5b's own measured real-condition value
    /// (~5% width, ~3.5% height on `plains_black`) so the curve names the
    /// exact real-world case it exists to explain, not just an isotropic
    /// stand-in for it.
    public static IReadOnlyList<CropScaleLevel> DefaultLevels { get; } = BuildDefaultLevels();

    private static IReadOnlyList<CropScaleLevel> BuildDefaultLevels()
    {
        float[] isotropic = [0.00f, 0.01f, 0.02f, 0.03f, 0.04f, 0.05f, 0.06f, 0.08f, 0.10f, 0.15f, 0.20f, -0.02f, -0.05f, -0.08f];
        var levels = new List<CropScaleLevel>(isotropic.Length + 1);
        foreach (var f in isotropic)
        {
            levels.Add(new CropScaleLevel(f, f, $"{f:+0.00;-0.00;0.00}"));
        }

        levels.Add(new CropScaleLevel(0.05f, 0.035f, "real (plains_black, B5b)"));
        return levels;
    }
}

/// Runs the SAME per-artwork measurement B2's `RoundTripGateRunner` uses
/// (`RoundTripMeasurement.Measure`, via `HashCardIdentifier.Identify` --
/// never a third query implementation), but against a `CropScaleTransform`-
/// distorted copy of each sampled render instead of the render itself, once
/// per level in `CropScaleExperimentOptions.Levels`. Each sampled artwork's
/// JPEG is decoded exactly ONCE and reused across every level -- only the
/// (cheap) crop-scale transform and the (already-brute-force-cheap)
/// identify call repeat per level, matching CLAUDE.md's measured per-query
/// cost.
public static class CropScaleExperimentRunner
{
    public static CropScaleExperimentSummary Run(HashIndexData index, HashCardIdentifier identifier, CropScaleExperimentOptions options)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(identifier);
        ArgumentNullException.ThrowIfNull(options);

        var nonLandEntries = index.Entries.Where(e => !e.IsBasicLand).ToList();
        var sample = RoundTripSampler.SelectUniformSample(nonLandEntries, options.SampleSize, options.Seed);

        var decoded = new List<(HashIndexEntry Entry, Mat Color)>(sample.Count);
        var missing = new List<string>();
        try
        {
            foreach (var entry in sample)
            {
                var imagePath = ImageCache.GetImagePath(options.CacheDir, entry.ArtworkId);
                if (!File.Exists(imagePath))
                {
                    missing.Add(entry.ArtworkId);
                    continue;
                }

                var color = Cv2.ImRead(imagePath, ImreadModes.Color);
                if (color.Empty())
                {
                    color.Dispose();
                    missing.Add(entry.ArtworkId);
                    continue;
                }

                decoded.Add((entry, color));
            }

            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    $"CropScaleExperimentRunner: {missing.Count} sampled artwork(s) had no usable cache image " +
                    $"(e.g. {missing[0]}) -- the crop-scale curve needs a complete cache, unlike the round-trip " +
                    "gate, which tolerates gaps via ImageAvailable. Re-run once the cache is complete.");
            }

            var points = new List<CropScalePoint>(options.Levels.Count);
            foreach (var level in options.Levels)
            {
                var outcomes = new List<RoundTripSampleOutcome>(decoded.Count);
                foreach (var (entry, color) in decoded)
                {
                    using var scaled = CropScaleTransform.Apply(color, level.WidthInsetFraction, level.HeightInsetFraction);
                    var card = CardImageLoader.ToRectifiedCard(scaled);
                    outcomes.Add(RoundTripMeasurement.Measure(identifier, index, card, entry, options.MaxCandidates));
                }

                var summary = new RoundTripGateSummary(outcomes, options.Seed, LandSampleSize: 0, NonLandSampleSize: outcomes.Count);
                points.Add(new CropScalePoint(
                    level.WidthInsetFraction, level.HeightInsetFraction, level.Label, RoundTripGateStatistics.From(summary)));
            }

            return new CropScaleExperimentSummary(points, options.Seed, decoded.Count);
        }
        finally
        {
            foreach (var (_, color) in decoded)
            {
                color.Dispose();
            }
        }
    }
}
