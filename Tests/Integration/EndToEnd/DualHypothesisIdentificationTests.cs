using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Detection;
using LoreFetch.Core.Identification;
using LoreFetch.Core.Scanning;
using LoreFetch.Lab;
using LoreFetch.Lab.Images;
using LoreFetch.Lab.Synthetic;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;
using Xunit;

namespace LoreFetch.Tests.Integration.EndToEnd;

/// Package DH: dual-hypothesis identification
/// (`Core.Scanning.DualHypothesisIdentification`), exercised against a REAL
/// Scryfall render composited into a synthetic camera frame
/// (`SyntheticFrameGenerator`, package B7) -- the same real-artifact-gated
/// pattern `RealImplementationSet` uses, kept self-contained here rather
/// than reusing that type's private `RealFixtures` (a different assembly's
/// internal nested type).
///
/// The central case reproduces E1a's own failure mode directly rather than
/// hoping a detector run on a synthetic frame happens to land inset: it
/// takes the REAL detected quad (well-framed) and shrinks it by the exact
/// inverse of `QuadExpansion`'s committed correction factors about its own
/// centroid -- simulating a detector that landed on the card's INNER
/// border edge, exactly as E1a found on `tight_white` and the black mat.
public class DualHypothesisIdentificationTests
{
    private const float HeightInches = 15f;
    private const int MaxCandidates = 5;

    [Fact]
    public void Identify_WellFramedQuad_IdentifiesTheCorrectArtwork()
    {
        var (fixtures, reason) = LoadFixtures();
        if (fixtures is null)
        {
            RealArtifactGate.SkipOrFail(reason!);
            return;
        }

        using var frame = BuildSyntheticFrame(fixtures);
        var detector = new ContourCardDetector(NullLogger<ContourCardDetector>.Instance);
        var detected = detector.Detect(frame, maxCards: 1);
        Assert.Single(detected);

        var rectifier = new PerspectiveRectifier();
        var outcome = DualHypothesisIdentification.Identify(frame, detected[0], rectifier, fixtures.Identifier, MaxCandidates);

        Assert.NotEmpty(outcome.Candidates);
        Assert.Equal(fixtures.ArtworkId, outcome.Candidates[0].ArtworkId);
    }

    [Fact]
    public void Identify_InsetQuad_RecoversTheCorrectArtwork_OnlyWithTheExpandedHypothesis()
    {
        var (fixtures, reason) = LoadFixtures();
        if (fixtures is null)
        {
            RealArtifactGate.SkipOrFail(reason!);
            return;
        }

        using var frame = BuildSyntheticFrame(fixtures);
        var detector = new ContourCardDetector(NullLogger<ContourCardDetector>.Instance);
        var detected = detector.Detect(frame, maxCards: 1);
        Assert.Single(detected);

        var trueQuad = detected[0];

        // The card's inner-border quad: the exact inverse of
        // `QuadExpansion`'s own committed correction, about the same
        // centroid -- what a detector that landed on the border's INNER
        // edge (E1a's finding) would have returned instead of `trueQuad`.
        var insetQuad = QuadExpansion.Expand(
            trueQuad,
            1f / QuadExpansion.BorderWidthCorrectionFactor,
            1f / QuadExpansion.BorderHeightCorrectionFactor);

        var rectifier = new PerspectiveRectifier();

        // Baseline-only: rectify+identify the inset quad directly, with no
        // expanded hypothesis at all -- reproduces the noise-level miss
        // E1a measured (~290-350) before asserting dual recovers it.
        var baselineOnlyCard = rectifier.Rectify(frame, insetQuad);
        var baselineOnlyCandidates = fixtures.Identifier.Identify(baselineOnlyCard, MaxCandidates);
        var baselineOnlyCorrect = baselineOnlyCandidates.Count > 0
            && string.Equals(baselineOnlyCandidates[0].ArtworkId, fixtures.ArtworkId, StringComparison.Ordinal);

        Assert.False(
            baselineOnlyCorrect,
            "the inset-only baseline was expected to miss (E1a's own failure mode) -- if it now matches, " +
            "this test's inset is no longer reproducing that failure and needs a larger inset to stay meaningful.");

        var outcome = DualHypothesisIdentification.Identify(frame, insetQuad, rectifier, fixtures.Identifier, MaxCandidates);

        Assert.NotEmpty(outcome.Candidates);
        Assert.Equal(fixtures.ArtworkId, outcome.Candidates[0].ArtworkId);
        Assert.Equal(IdentificationHypothesis.Expanded, outcome.Winner);
        Assert.False(outcome.ExpandedHypothesisSkipped);
    }

    [Fact]
    public void Identify_ExpandedQuadWouldLeaveTheFrame_SkipsCleanlyAndFallsBackToAsDetected()
    {
        var (fixtures, reason) = LoadFixtures();
        if (fixtures is null)
        {
            RealArtifactGate.SkipOrFail(reason!);
            return;
        }

        // Flush against the frame's own top-left corner: any outward
        // expansion about the centroid necessarily pushes the TL corner
        // negative on both axes, so TryExpand must refuse it.
        var quad = new CardQuad(
            TL: new PointF2(0, 0),
            TR: new PointF2(200, 0),
            BR: new PointF2(200, 280),
            BL: new PointF2(0, 280));

        using var frameBgr = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(120, 120, 120));
        using var frame = FrameMat.FromMat(frameBgr);

        var rectifier = new PerspectiveRectifier();
        var outcome = DualHypothesisIdentification.Identify(frame, quad, rectifier, fixtures.Identifier, MaxCandidates);

        Assert.True(outcome.ExpandedHypothesisSkipped);
        Assert.Equal(IdentificationHypothesis.AsDetected, outcome.Winner);

        // The as-detected hypothesis's own candidates, unmodified -- proves
        // the fallback did not silently swap in something else.
        var asDetectedCard = rectifier.Rectify(frame, quad);
        var expectedCandidates = fixtures.Identifier.Identify(asDetectedCard, MaxCandidates);
        Assert.Equal(expectedCandidates.Count, outcome.Candidates.Count);
    }

    private static CameraFrame BuildSyntheticFrame(RealFixtures fixtures) =>
        SyntheticFrameGenerator.Generate(fixtures.SampleCardBgr, HeightInches).Frame;

    /// Everything this test class needs from disk, loaded once per test and
    /// gated together -- the committed index (for the real
    /// `HashCardIdentifier`) and one real Scryfall render pulled from the
    /// local cache for a non-land artwork the index actually contains.
    /// Deliberately duplicates `RealImplementationSet.RealFixtures`'s own
    /// loading recipe rather than reusing it (that type is `private` to a
    /// sibling test file in the same assembly, but keeping this test's own
    /// fixture needs self-contained and explicit is worth the small
    /// duplication -- same call made in `RealImplementationSet`'s own doc
    /// comment for its `ResolveCacheDir` copy).
    private sealed class RealFixtures
    {
        public required HashCardIdentifier Identifier { get; init; }

        public required Mat SampleCardBgr { get; init; }

        public required string ArtworkId { get; init; }
    }

    private static (RealFixtures? Fixtures, string? SkipReason) LoadFixtures()
    {
        if (!RepoPaths.TryFindRepoRoot(out var repoRoot))
        {
            return (null, "could not locate the repository root to find the committed index.");
        }

        var indexPath = Path.Combine(repoRoot!, "data", "index", "cards.lfidx");
        if (!File.Exists(indexPath))
        {
            return (null, $"committed hash index not present at \"{indexPath}\".");
        }

        var cacheDir = ResolveCacheDir();
        if (!Directory.Exists(cacheDir))
        {
            return (null, $"Scryfall image cache not present at \"{cacheDir}\" -- card imagery can never be committed.");
        }

        var data = HashIndexFile.Read(indexPath);
        if (data.Entries.Count == 0)
        {
            return (null, $"committed hash index at \"{indexPath}\" has zero entries.");
        }

        HashIndexEntry? sample = null;
        foreach (var entry in data.Entries)
        {
            if (entry.IsBasicLand)
            {
                continue;
            }

            if (File.Exists(ImageCache.GetImagePath(cacheDir, entry.ArtworkId)))
            {
                sample = entry;
                break;
            }
        }

        if (sample is null)
        {
            return (null, $"Scryfall image cache at \"{cacheDir}\" has no cached render for any indexed, " +
                "non-land artwork -- it may still be populating.");
        }

        var sampleImagePath = ImageCache.GetImagePath(cacheDir, sample.Value.ArtworkId);
        var sampleCardBgr = Cv2.ImRead(sampleImagePath, ImreadModes.Color);
        if (sampleCardBgr.Empty())
        {
            sampleCardBgr.Dispose();
            return (null, $"cached render at \"{sampleImagePath}\" failed to decode.");
        }

        var identifier = new HashCardIdentifier(data, NullLogger<HashCardIdentifier>.Instance);
        return (new RealFixtures { Identifier = identifier, SampleCardBgr = sampleCardBgr, ArtworkId = sample.Value.ArtworkId }, null);
    }

    /// Same resolution order as `RealImplementationSet.RealFixtures.ResolveCacheDir`
    /// (itself a copy of `RoundTripGateTests`'s): `LOREFETCH_SCRYFALL_CACHE`
    /// first, then `~/LoreFetchData/scryfall-cache`, then (Windows only)
    /// `C:\LoreFetchData\scryfall-cache`.
    private static string ResolveCacheDir()
    {
        var overridePath = Environment.GetEnvironmentVariable("LOREFETCH_SCRYFALL_CACHE");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return overridePath;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var homeCandidate = Path.Combine(home, "LoreFetchData", "scryfall-cache");
        if (Directory.Exists(homeCandidate))
        {
            return homeCandidate;
        }

        if (OperatingSystem.IsWindows())
        {
            var windowsCandidate = Path.Combine("C:" + Path.DirectorySeparatorChar, "LoreFetchData", "scryfall-cache");
            if (Directory.Exists(windowsCandidate))
            {
                return windowsCandidate;
            }
        }

        return homeCandidate;
    }
}
