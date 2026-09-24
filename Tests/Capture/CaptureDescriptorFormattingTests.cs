using LoreFetch.Capture;
using Xunit;

namespace LoreFetch.Tests.Capture;

/// `CaptureDescriptorFormatting` is the one place `PreferredDeviceId`'s
/// format is defined (its own doc comment). These tests pin that format
/// directly, independent of `CaptureDeviceSelectorTests`' round-trip
/// coverage through `SelectDevice`.
public class CaptureDescriptorFormattingTests
{
    // xunit theory data must be publicly visible, and CaptureBackend is
    // internal (deliberately — see its doc comment), so each backend gets
    // its own Fact rather than a [Theory]/[InlineData] over the enum.
    [Fact]
    public void FormatDeviceId_PrefixesWithDirectShow() => AssertPrefixed(CaptureBackend.DirectShow, "DirectShow");

    [Fact]
    public void FormatDeviceId_PrefixesWithMediaFoundation() => AssertPrefixed(CaptureBackend.MediaFoundation, "MediaFoundation");

    [Fact]
    public void FormatDeviceId_PrefixesWithVideoForWindows() => AssertPrefixed(CaptureBackend.VideoForWindows, "VideoForWindows");

    private static void AssertPrefixed(CaptureBackend backend, string expectedPrefix)
    {
        var descriptor = new CaptureDescriptor(backend, @"\\?\usb#vid_046d&pid_082d", "Cam", []);

        var id = CaptureDescriptorFormatting.FormatDeviceId(descriptor);

        Assert.Equal($"{expectedPrefix}:\\\\?\\usb#vid_046d&pid_082d", id);
    }

    [Fact]
    public void FormatList_IncludesEveryDescriptorsFullCharacteristicList()
    {
        var descriptors = new[]
        {
            new CaptureDescriptor(CaptureBackend.DirectShow, "d1", "Cam A", [new CaptureCharacteristic(1920, 1080, "JPEG", 30.0)]),
            new CaptureDescriptor(CaptureBackend.MediaFoundation, "d2", "Cam B", [new CaptureCharacteristic(1920, 1080, "YUYV", 5.0)]),
        };

        var list = CaptureDescriptorFormatting.FormatList(descriptors);

        Assert.Contains("DirectShow:d1", list);
        Assert.Contains("Cam A", list);
        Assert.Contains("1920x1080 JPEG @30fps", list);
        Assert.Contains("MediaFoundation:d2", list);
        Assert.Contains("Cam B", list);
        Assert.Contains("1920x1080 YUYV @5fps", list);
    }

    [Fact]
    public void FormatCharacteristics_EmptyList_SaysSoRatherThanBeingBlank()
    {
        Assert.Equal("(no characteristics)", CaptureDescriptorFormatting.FormatCharacteristics([]));
    }
}
