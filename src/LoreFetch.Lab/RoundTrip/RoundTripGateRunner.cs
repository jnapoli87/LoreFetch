using LoreFetch.Core.Identification;
using LoreFetch.Lab.Images;
using OpenCvSharp;

namespace LoreFetch.Lab.RoundTrip;

public sealed class RoundTripGateOptions
{
    public required string CacheDir { get; init; }

    /// "About 200 renders spread across the ladder" (stream-b-identification.md
    /// "B2"): land and non-land counts are separate knobs rather than one
    /// total + a fraction, so a caller can see and vary each independently.
    public int LandSampleSize { get; init; } = 20;

    public int NonLandSampleSize { get; init; } = 180;

    public int Seed { get; init; } = 20260922;

    public int MaxCandidates { get; init; } = 3;
}

/// One completed run: every sampled outcome (including missing-image
/// placeholders) plus the exact sampling parameters that produced it, so a
/// caller building `RoundTripGateStatistics` or a report never has to
/// re-derive them.
public sealed record RoundTripGateSummary(
    IReadOnlyList<RoundTripSampleOutcome> Outcomes,
    int Seed,
    int LandSampleSize,
    int NonLandSampleSize);

/// Runs `RoundTripSampler` + `RoundTripMeasurement` end to end against a
/// real, already-loaded index and a real external image cache. The only
/// I/O this type performs itself is the per-artwork JPEG decode
/// (`Cv2.ImRead`) -- sampling and measurement are both delegated, so this
/// type is pure orchestration.
public static class RoundTripGateRunner
{
    public static RoundTripGateSummary Run(HashIndexData index, HashCardIdentifier identifier, RoundTripGateOptions options)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(identifier);
        ArgumentNullException.ThrowIfNull(options);

        var sample = RoundTripSampler.SelectStratifiedSample(
            index.Entries, options.LandSampleSize, options.NonLandSampleSize, options.Seed);

        var outcomes = new List<RoundTripSampleOutcome>(sample.Count);

        foreach (var entry in sample)
        {
            var imagePath = ImageCache.GetImagePath(options.CacheDir, entry.ArtworkId);
            if (!File.Exists(imagePath))
            {
                outcomes.Add(RoundTripMeasurement.MissingImageOutcome(entry));
                continue;
            }

            using var color = Cv2.ImRead(imagePath, ImreadModes.Color);
            if (color.Empty())
            {
                outcomes.Add(RoundTripMeasurement.MissingImageOutcome(entry));
                continue;
            }

            var card = CardImageLoader.ToRectifiedCard(color);
            outcomes.Add(RoundTripMeasurement.Measure(identifier, index, card, entry, options.MaxCandidates));
        }

        return new RoundTripGateSummary(outcomes, options.Seed, options.LandSampleSize, options.NonLandSampleSize);
    }
}
