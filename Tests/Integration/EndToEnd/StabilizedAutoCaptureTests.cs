using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Detection;
using LoreFetch.Core.Trigger;
using Xunit;

namespace LoreFetch.Tests.Integration.EndToEnd;

/// Detection feeding the real `AutoCaptureTrigger`, the way `ScanPipeline`
/// wires them. A card the detector finds only on alternate frames resets the
/// trigger's settle timer on every miss, so auto-capture never fires.
/// `StabilizingCardDetector` is what makes it fire. Time is passed in
/// explicitly at 30 fps, so nothing here depends on the test machine's
/// scheduling.
public class StabilizedAutoCaptureTests
{
    private const int Frames = 45; // 1.5 s at 30 fps, three settle windows.

    private static readonly CardQuad Card = new(
        new PointF2(100, 100), new PointF2(200, 100), new PointF2(200, 240), new PointF2(100, 240));

    [Fact]
    public void FlickeringCard_WithoutStabilizer_NeverAutoCaptures()
    {
        Assert.Equal(0, CountAutoCaptures(new FlickeringDetector(Card)));
    }

    [Fact]
    public void FlickeringCard_WithStabilizer_AutoCapturesOnce()
    {
        Assert.Equal(1, CountAutoCaptures(new StabilizingCardDetector(new FlickeringDetector(Card))));
    }

    private static int CountAutoCaptures(ICardDetector detector)
    {
        var settings = new ScanSettings { ExpectedCount = 1 };
        var trigger = new AutoCaptureTrigger(settings);
        using var frame = new CameraFrame(new byte[3], 1, 1, 3, PixelLayout.Bgr24, DateTimeOffset.UnixEpoch, pool: null);

        var fires = 0;
        for (var i = 0; i < Frames; i++)
        {
            var now = DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(i / 30.0);
            var quads = detector.Detect(frame, maxCards: 9);
            if (trigger.Evaluate(quads, settings.ExpectedCount, now))
            {
                fires++;
            }
        }

        return fires;
    }

    /// Finds the card on even frames and misses it on odd ones.
    private sealed class FlickeringDetector(CardQuad card) : ICardDetector
    {
        private int _call;

        public IReadOnlyList<CardQuad> Detect(CameraFrame frame, int maxCards) =>
            _call++ % 2 == 0 ? [card] : [];
    }
}
