using LoreFetch.Capture;
using Xunit;

namespace LoreFetch.Tests.StreamC;

/// `WebcamFrameSourceFactory.BuildDescription` is `internal static` and
/// pure specifically so this can be tested without opening a device — see
/// its doc comment. Every case here uses negotiated numbers that differ
/// from the requested constants (1920x1080 @30fps), so a regression that
/// silently reports the requested constants instead of what was actually
/// negotiated is caught by content, not just by "a string came back."
public class WebcamFrameSourceFactoryTests
{
    [Fact]
    public void BuildDescription_UsesNegotiatedNumbers_NotRequestedConstants()
    {
        var descriptor = new CaptureDescriptor(CaptureBackend.DirectShow, "dev-1", "HD Pro Webcam C920", []);
        var characteristic = new CaptureCharacteristic(1920, 1080, "JPEG", 30.0);

        var description = WebcamFrameSourceFactory.BuildDescription(descriptor, characteristic);

        Assert.Contains("HD Pro Webcam C920", description);
        Assert.Contains("1920x1080", description);
        Assert.Contains("MJPG", description);
        Assert.Contains("@30fps", description);
        Assert.Contains("DirectShow", description);
    }

    [Fact]
    public void BuildDescription_DifferentNegotiatedFormat_ReflectsIt_NotTheRequestedOne()
    {
        // A device that only ever offered 60 fps at the required resolution
        // — if this ever printed "@30fps" it would mean the description was
        // built from CaptureDeviceSelector's constants rather than the
        // characteristic CreateAsync actually negotiated.
        var descriptor = new CaptureDescriptor(CaptureBackend.MediaFoundation, "dev-2", "Some Other Camera", []);
        var characteristic = new CaptureCharacteristic(1920, 1080, "JPEG", 60.0);

        var description = WebcamFrameSourceFactory.BuildDescription(descriptor, characteristic);

        Assert.Contains("@60fps", description);
        Assert.DoesNotContain("@30fps", description);
        Assert.Contains("MediaFoundation", description);
    }

    [Fact]
    public void BuildDescription_NonIntegerFps_IsFormattedWithoutTrailingZeros()
    {
        var descriptor = new CaptureDescriptor(CaptureBackend.DirectShow, "dev-1", "Cam", []);
        var characteristic = new CaptureCharacteristic(1920, 1080, "JPEG", 30.5);

        var description = WebcamFrameSourceFactory.BuildDescription(descriptor, characteristic);

        Assert.Contains("@30.5fps", description);
    }
}
