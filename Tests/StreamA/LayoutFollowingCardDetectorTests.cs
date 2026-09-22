using LoreFetch.App.Fakes;
using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Fakes;
using Xunit;

namespace LoreFetch.Tests.StreamA;

/// <summary>
/// A10-prep item 4a: <see cref="LayoutFollowingCardDetector"/> must make the
/// NEXT <c>Detect</c> call reflect whatever <see cref="ScanSettings.ExpectedCount"/>
/// is right now, not whatever it was when the detector was constructed.
/// </summary>
public class LayoutFollowingCardDetectorTests
{
    [Fact]
    public void Detect_FollowsCurrentExpectedCount_AcrossCalls()
    {
        var settings = new ScanSettings { ExpectedCount = 1 };
        var stub = new StubCardDetector(cardCount: 1);
        var detector = new LayoutFollowingCardDetector(stub, settings);

        using var frame = MakeFrame();

        var quads1 = detector.Detect(frame, maxCards: 9);
        Assert.Single(quads1);

        settings.ExpectedCount = 3;
        var quads3 = detector.Detect(frame, maxCards: 9);
        Assert.Equal(3, quads3.Count);

        settings.ExpectedCount = 9;
        var quads9 = detector.Detect(frame, maxCards: 9);
        Assert.Equal(9, quads9.Count);

        // And back down again — proves this isn't a one-way ratchet.
        settings.ExpectedCount = 1;
        var quadsBackTo1 = detector.Detect(frame, maxCards: 9);
        Assert.Single(quadsBackTo1);
    }

    [Fact]
    public void Detect_StillClampsToMaxCards_RegardlessOfExpectedCount()
    {
        // maxCards is MaxDetectionCards from the pipeline (always 9,
        // independent of ExpectedCount per orchestration finding V12) — this
        // wrapper must not break that clamp.
        var settings = new ScanSettings { ExpectedCount = 9 };
        var stub = new StubCardDetector(cardCount: 1);
        var detector = new LayoutFollowingCardDetector(stub, settings);

        using var frame = MakeFrame();
        var quads = detector.Detect(frame, maxCards: 3);

        Assert.Equal(3, quads.Count);
    }

    private static CameraFrame MakeFrame() =>
        new(
            buffer: new byte[300 * 300 * 3],
            width: 300,
            height: 300,
            stride: 300 * 3,
            layout: PixelLayout.Bgr24,
            capturedAt: DateTimeOffset.UtcNow,
            pool: null);
}
