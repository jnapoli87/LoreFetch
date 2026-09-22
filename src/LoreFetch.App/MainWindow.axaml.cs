using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Scanning;

namespace LoreFetch.App;

/// The app's single window. A1 gave it the status line and the composition
/// hookup; A2 adds the live preview — one reused `WriteableBitmap`, filled
/// from `IScanPipeline.FrameProcessed` frames and blitted at a throttled
/// ~15 fps. The quad overlay, expected-count selector and cohort grid land
/// in later packages.
public partial class MainWindow : Window
{
    /// ~15 fps: plenty for the eye, and comfortably inside every measured
    /// WriteableBitmap-behind-Image ceiling (docs/stream-a-ui.md A1,
    /// "Corrected" — nothing here claims a number was ever measured beyond
    /// what's cited there, and 15 is well inside it). This only gates how
    /// OFTEN a render is requested — TopLevel.RequestAnimationFrame is
    /// itself render-loop-paced on top of that.
    private static readonly TimeSpan RenderInterval = TimeSpan.FromSeconds(1.0 / 15.0);

    private readonly object _frameLock = new();

    // --- Fields touched by BOTH threads, guarded by _frameLock -----------
    //
    // OnFrameProcessed (the pipeline's background thread) copies raw,
    // UNCONVERTED frame bytes here every time it fires. Conversion happens
    // later, on the UI thread in OnRenderFrame, once the destination
    // WriteableBitmap is locked and its real RowBytes is known. CONTRACTS.md
    // guarantees a frame's own contents are valid only for the duration of
    // the FrameProcessed callback, so the copy — not the conversion — is
    // what has to happen inside it; the pipeline recycles the pooled buffer
    // the instant the handler returns.
    private byte[]? _stagingBuffer;
    private int _pendingWidth;
    private int _pendingHeight;
    private int _pendingStride;
    private PixelLayout _pendingLayout;
    private bool _hasPendingFrame;

    // --- UI-thread-only fields --------------------------------------------
    //
    // ONE WriteableBitmap for the life of the app (A2). Reallocated only
    // when the frame's Width/Height changes — never per frame, which is the
    // per-frame-allocation trap CLAUDE.md's "Avalonia preview" note warns
    // about ("the documented cause of every 'camera preview is choppy'
    // report").
    private WriteableBitmap? _bitmap;
    private PixelSize _bitmapSize;

    // Reused conversion output, shaped to the CURRENT bitmap's
    // Lock().RowBytes * Height. Reallocated alongside `_bitmap`, and also if
    // RowBytes itself ever changes for the same size — docs/stream-a-ui.md
    // notes RowBytes comes from SKImageInfo.RowBytes on the Skia backend and
    // is unpadded there, but nothing here assumes that holds everywhere.
    private byte[]? _convertedBuffer;
    private int _convertedBufferRowBytes;

    private TopLevel? _topLevel;
    private long _renderScheduled;
    private DateTime _lastRenderRequestUtc = DateTime.MinValue;

    private IScanPipeline? _pipeline;

    // Parameterless constructor required by the Avalonia XAML previewer and
    // the compiled-XAML loader.
    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(AppSession session)
        : this()
    {
        ArgumentNullException.ThrowIfNull(session);

        // The ONE place SourceDescription is read — never composed from
        // parts, per the global A override.
        StatusText.Text = session.Pipeline.SourceDescription;

        _pipeline = session.Pipeline;
        _pipeline.FrameProcessed += OnFrameProcessed;
        Closed += OnClosed;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_pipeline is not null)
        {
            _pipeline.FrameProcessed -= OnFrameProcessed;
            _pipeline = null;
        }
    }

    /// Raised on the pipeline's background thread for every processed frame
    /// (CONTRACTS.md `IScanPipeline.FrameProcessed`). `frame` is valid only
    /// for the duration of this call, so the only thing this does with it is
    /// copy its bytes into `_stagingBuffer` before returning — no Avalonia
    /// object is touched here, and nothing here ever retains `frame` itself.
    private void OnFrameProcessed(CameraFrame frame, DetectionSnapshot snapshot)
    {
        lock (_frameLock)
        {
            var needed = frame.Stride * frame.Height;
            if (_stagingBuffer is null || _stagingBuffer.Length < needed)
            {
                _stagingBuffer = new byte[needed];
            }

            frame.Pixels.Span.CopyTo(_stagingBuffer);

            _pendingWidth = frame.Width;
            _pendingHeight = frame.Height;
            _pendingStride = frame.Stride;
            _pendingLayout = frame.Layout;
            _hasPendingFrame = true;
        }

        ScheduleRenderIfDue();
    }

    /// Coalesces to ~15 fps AND to at most one outstanding request. The
    /// frame source can call OnFrameProcessed far faster than 15 fps (a
    /// synthetic folder source on a short timer, or a real 30 fps camera),
    /// so both gates are needed: the time check limits how often a render is
    /// asked for, and the CompareExchange stops a second ask piling up
    /// before the render loop gets to the first one. Safe to call from the
    /// background thread — TopLevel.RequestAnimationFrame is the recommended
    /// marshal point precisely because it is (docs/stream-a-ui.md A1).
    private void ScheduleRenderIfDue()
    {
        var topLevel = _topLevel ??= TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return; // Not attached to a TopLevel yet; the next frame retries.
        }

        var now = DateTime.UtcNow;
        if (now - _lastRenderRequestUtc < RenderInterval)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _renderScheduled, 1, 0) != 0)
        {
            return;
        }

        _lastRenderRequestUtc = now;
        topLevel.RequestAnimationFrame(OnRenderFrame);
    }

    /// Runs on the UI thread. Order matters and is exactly
    /// Lock() -> convert -> dispose the lock -> InvalidateVisual(): the
    /// Skia backend caches an SKImage snapshot that only the lock's Dispose
    /// invalidates, so invalidating before disposing (or holding the lock
    /// across InvalidateVisual) renders the FIRST frame forever
    /// (docs/stream-a-ui.md A1).
    private void OnRenderFrame(TimeSpan _)
    {
        Interlocked.Exchange(ref _renderScheduled, 0);

        byte[] staging;
        int width, height, stride;
        PixelLayout layout;

        lock (_frameLock)
        {
            if (!_hasPendingFrame || _stagingBuffer is null)
            {
                return;
            }

            staging = _stagingBuffer;
            width = _pendingWidth;
            height = _pendingHeight;
            stride = _pendingStride;
            layout = _pendingLayout;
            _hasPendingFrame = false;
        }

        EnsureBitmap(width, height);

        using (var framebuffer = _bitmap!.Lock())
        {
            EnsureConvertedBuffer(framebuffer.RowBytes, height);
            PixelConvert.ToBgra32(staging, width, height, stride, layout, _convertedBuffer!, framebuffer.RowBytes);
            Marshal.Copy(_convertedBuffer!, 0, framebuffer.Address, framebuffer.RowBytes * height);
        }

        PreviewImage.InvalidateVisual();
    }

    /// Reallocates the ONE `WriteableBitmap` only when the frame's
    /// Width/Height changes. Rebinding `Image.Source` here is the one
    /// legitimate exception to "never rebind Source" — it happens only
    /// alongside a genuinely new bitmap instance, never for the same
    /// instance on a per-frame basis.
    private void EnsureBitmap(int width, int height)
    {
        var size = new PixelSize(width, height);
        if (_bitmap is not null && _bitmapSize.Equals(size))
        {
            return;
        }

        _bitmap?.Dispose();
        _bitmap = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        _bitmapSize = size;
        PreviewImage.Source = _bitmap;
    }

    private void EnsureConvertedBuffer(int rowBytes, int height)
    {
        var needed = rowBytes * height;
        if (_convertedBuffer is not null && _convertedBufferRowBytes == rowBytes && _convertedBuffer.Length >= needed)
        {
            return;
        }

        _convertedBuffer = new byte[needed];
        _convertedBufferRowBytes = rowBytes;
    }
}
