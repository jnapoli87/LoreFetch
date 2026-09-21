namespace LoreFetch.Core.Abstractions;

public sealed class ScanSettings
{
    public int ExpectedCount { get; set; } = 1; // 1, 3 or 9
    public int SettleMilliseconds { get; set; } = 500;
    public int GoodDistance { get; set; } // from stream B's thresholds file
    public int OkDistance { get; set; }   // from stream B's thresholds file

    /// Auto-capture is OFF on a first run: it is the surprising mode, and
    /// without a flag here the UI has no way to expose the toggle at all.
    public bool AutoCaptureEnabled { get; set; } = false;

    /// The settle condition's epsilon: movement beyond this many pixels
    /// between snapshots resets the settle timer. 4 px is ~0.03" at the
    /// settled ~9.75" height (~139 px/inch) — above contour jitter, far
    /// below hand movement.
    public int MovementTolerancePixels { get; set; } = 4;

    /// Device-failure detection. Device-in-use, permission-denied and unplug
    /// are NOT exceptions from the capture backend — all three present as
    /// frames that simply never arrive, so these two timeouts are the only
    /// mechanism that turns silence into a FrameSourceException.
    /// The first-frame budget is generous because Media Foundation has been
    /// measured at 5.7 s to first frame at 1080p.
    public int FirstFrameTimeoutMs { get; set; } = 10_000;

    public int FrameWatchdogMs { get; set; } = 2_000;

    private int _cameraRotationDegrees = 90;

    /// 0, 90, 180 or 270 only — the setter throws ArgumentOutOfRangeException
    /// on anything else, because nothing else is implementable.
    public int CameraRotationDegrees
    {
        get => _cameraRotationDegrees;
        set
        {
            if (value is not (0 or 90 or 180 or 270))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    "CameraRotationDegrees must be one of 0, 90, 180 or 270.");
            }

            _cameraRotationDegrees = value;
        }
    }

    /// The capture backend's own opaque device identity, prefixed with the
    /// backend that produced it, e.g. "dshow:\\?\usb#vid_046d...".
    /// The prefix matters: Windows enumeration concatenates three backends,
    /// so one C920 yields up to three descriptors, and an unprefixed id
    /// silently stops matching if the preference order ever changes.
    /// Null means "first usable device".
    public string? PreferredDeviceId { get; set; }
}
