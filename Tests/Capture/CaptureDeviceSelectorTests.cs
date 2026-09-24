using LoreFetch.Capture;
using LoreFetch.Core.Abstractions;
using Xunit;

namespace LoreFetch.Tests.Capture;

/// Exercises `CaptureDeviceSelector` entirely over plain `CaptureDescriptor`
/// data — no FlashCap device, no FlashCap type anywhere in this file. This
/// is the split docs/design/capture.md's "Done when" calls for: "the
/// FlashCap-facing shim is the only code that cannot be tested."
public class CaptureDeviceSelectorTests
{
    private static CaptureCharacteristic Match1080pJpeg(double fps) => new(1920, 1080, "JPEG", fps);

    private static CaptureDescriptor Descriptor(CaptureBackend backend, string id, string name, params CaptureCharacteristic[] characteristics) =>
        new(backend, id, name, characteristics);

    [Fact]
    public void SelectDevice_ExactMatch_1080pJpeg30Fps_IsSelected()
    {
        var descriptor = Descriptor(CaptureBackend.DirectShow, "dev-1", "HD Pro Webcam C920", Match1080pJpeg(30.0));

        var selection = CaptureDeviceSelector.SelectDevice([descriptor], preferredDeviceId: null);

        Assert.Equal(0, selection.DescriptorIndex);
        Assert.Equal(0, selection.CharacteristicIndex);
        Assert.Equal(1920, selection.Characteristic.Width);
        Assert.Equal(1080, selection.Characteristic.Height);
        Assert.Equal("JPEG", selection.Characteristic.PixelFormat);
        Assert.Equal(30.0, selection.Characteristic.FramesPerSecond);
    }

    // 30000/1001 is the classic NTSC-style "29.97 fps" advertisement. The
    // rule is >= 30.0 exactly, so this must be rejected even though it
    // would display as "30fps" if naively rounded — the whole reason
    // docs/design/capture.md insists on `(double)c.FramesPerSecond >= 30.0`
    // rather than `== 30`.
    [Fact]
    public void SelectDevice_2997Fps_IsRejected_NoMatchingCharacteristic()
    {
        var descriptor = Descriptor(CaptureBackend.DirectShow, "dev-1", "Cam", Match1080pJpeg(30000.0 / 1001.0));

        var ex = Assert.Throws<FrameSourceException>(() => CaptureDeviceSelector.SelectDevice([descriptor], null));

        Assert.Contains("No enumerated device offers", ex.Message);
    }

    [Fact]
    public void SelectDevice_60Fps_IsAccepted()
    {
        var descriptor = Descriptor(CaptureBackend.DirectShow, "dev-1", "Cam", Match1080pJpeg(60.0));

        var selection = CaptureDeviceSelector.SelectDevice([descriptor], null);

        Assert.Equal(60.0, selection.Characteristic.FramesPerSecond);
    }

    [Fact]
    public void SelectDevice_Yuyv1080pAt5Fps_Throws_WithEnumeratedListInMessage()
    {
        var descriptor = Descriptor(CaptureBackend.DirectShow, "dev-1", "HD Pro Webcam C920", new CaptureCharacteristic(1920, 1080, "YUYV", 5.0));

        var ex = Assert.Throws<FrameSourceException>(() => CaptureDeviceSelector.SelectDevice([descriptor], null));

        Assert.Contains("1920x1080 YUYV @5fps", ex.Message);
        Assert.Contains("HD Pro Webcam C920", ex.Message);
        Assert.Contains("DirectShow:dev-1", ex.Message);
    }

    [Fact]
    public void SelectDevice_ZeroDescriptors_ThrowsDistinctDiagnosis_NotNoMatchingFormat()
    {
        var ex = Assert.Throws<FrameSourceException>(() => CaptureDeviceSelector.SelectDevice([], null));

        Assert.DoesNotContain("No enumerated device offers", ex.Message);
        Assert.Contains("no camera", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("permission", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelectDevice_BothBackendsMatch_DirectShowIsPreferredOverMediaFoundation()
    {
        var mediaFoundation = Descriptor(CaptureBackend.MediaFoundation, "mf-1", "Cam (MF)", Match1080pJpeg(30.0));
        var directShow = Descriptor(CaptureBackend.DirectShow, "ds-1", "Cam (DShow)", Match1080pJpeg(30.0));

        // Order in the list deliberately does not match preference order,
        // so a bug that just picks "the first descriptor" can't pass by
        // accident.
        var selection = CaptureDeviceSelector.SelectDevice([mediaFoundation, directShow], null);

        Assert.Equal(CaptureBackend.DirectShow, selection.Descriptor.Backend);
    }

    [Fact]
    public void SelectDevice_VideoForWindowsOnlyMatch_IsNeverChosen_Throws()
    {
        var descriptor = Descriptor(CaptureBackend.VideoForWindows, "vfw-1", "Cam (VfW)", Match1080pJpeg(30.0));

        var ex = Assert.Throws<FrameSourceException>(() => CaptureDeviceSelector.SelectDevice([descriptor], null));

        Assert.Contains("No enumerated device offers", ex.Message);
        Assert.Contains("VfW", ex.Message.Replace("Video for Windows", "VfW"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PreferredDeviceId_RoundTrips_FormatThenMatch()
    {
        var descriptor = Descriptor(CaptureBackend.DirectShow, @"\\?\usb#vid_046d&pid_082d", "HD Pro Webcam C920", Match1080pJpeg(30.0));
        var id = CaptureDescriptorFormatting.FormatDeviceId(descriptor);

        var selection = CaptureDeviceSelector.SelectDevice([descriptor], id);

        Assert.Equal(0, selection.DescriptorIndex);
        Assert.StartsWith("DirectShow:", id, StringComparison.Ordinal);
    }

    [Fact]
    public void PreferredDeviceId_Absent_Throws_ListingAvailableIds()
    {
        var descriptor = Descriptor(CaptureBackend.DirectShow, "dev-1", "Cam", Match1080pJpeg(30.0));
        var availableId = CaptureDescriptorFormatting.FormatDeviceId(descriptor);

        var ex = Assert.Throws<FrameSourceException>(
            () => CaptureDeviceSelector.SelectDevice([descriptor], "DirectShow:does-not-exist"));

        Assert.Contains(availableId, ex.Message);
        Assert.Contains("was not found", ex.Message);
    }

    [Fact]
    public void PreferredDeviceId_MatchesVideoForWindowsDescriptor_IsNeverChosen_Throws()
    {
        // Even an *explicit* preference for a VfW device must be refused —
        // "Video for Windows ignored" is an absolute rule, not just the
        // default search order's tie-break.
        var descriptor = Descriptor(CaptureBackend.VideoForWindows, "vfw-1", "Cam (VfW)", Match1080pJpeg(30.0));
        var id = CaptureDescriptorFormatting.FormatDeviceId(descriptor);

        var ex = Assert.Throws<FrameSourceException>(() => CaptureDeviceSelector.SelectDevice([descriptor], id));

        Assert.Contains("was not found", ex.Message);
    }

    [Fact]
    public void SelectDevice_ZeroDescriptors_WithPreferredId_StillGivesZeroDescriptorDiagnosis()
    {
        var ex = Assert.Throws<FrameSourceException>(() => CaptureDeviceSelector.SelectDevice([], "DirectShow:anything"));

        Assert.Contains("no camera", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
