using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using LoreFetch.Core.Abstractions;
using OpenCvSharp;

namespace LoreFetch.Core.Fakes;

/// Cycles the images in a directory on a timer, decoding each with
/// OpenCvSharp. This is the app's actual demo path, not a throwaway test
/// double — stream A builds its whole UI against it, and it is what
/// LoreFetch ships with when no camera is attached.
///
/// Newest-frame-only: a background loop decodes on the configured interval
/// and writes into a capacity-1 channel with `DropOldest`, so a slow
/// consumer sees the newest image, not a backlog. The dropped `CameraFrame`
/// is disposed in the channel's `itemDropped` callback — a plain bounded
/// channel does not do this on its own, and every dropped frame owns a
/// pooled buffer that would otherwise leak on every single drop.
public sealed class FolderFrameSource : IFrameSource
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp"];

    private readonly string[] _imagePaths;
    private readonly ArrayPool<byte> _pool;
    private readonly PeriodicTimer _timer;
    private readonly Channel<CameraFrame> _channel;
    private readonly CancellationTokenSource _stopCts = new();
    private readonly Task _produceLoop;

    private int _nextIndex;
    private int _disposed;

    private FolderFrameSource(
        string directoryName,
        string[] imagePaths,
        TimeSpan interval,
        FrameGeometry geometry,
        ArrayPool<byte> pool)
    {
        _imagePaths = imagePaths;
        _pool = pool;
        Geometry = geometry;
        Description = $"folder: {directoryName} ({imagePaths.Length} images)";

        _timer = new PeriodicTimer(interval);

        var channelOptions = new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        };
        _channel = Channel.CreateBounded<CameraFrame>(channelOptions, DisposeDroppedFrame);

        _produceLoop = Task.Run(() => ProduceAsync(_stopCts.Token));
    }

    /// Human-readable, for logs and the UI status line — what was actually
    /// opened (how many images decoded successfully), not what was requested.
    public string Description { get; }

    public FrameGeometry Geometry { get; }

    /// Opens `folder`, validating eagerly so a bad path or an empty/corrupt
    /// folder fails at startup rather than from inside the enumerator.
    /// `pool` defaults to `ArrayPool&lt;byte&gt;.Shared`; a caller may pass a
    /// wrapping pool (never a separate arena via `ArrayPool&lt;byte&gt;.Create()`)
    /// to observe rents and returns, e.g. in tests.
    public static FolderFrameSource Open(string folder, TimeSpan interval, ArrayPool<byte>? pool = null)
    {
        ArgumentNullException.ThrowIfNull(folder);

        if (!Directory.Exists(folder))
        {
            throw new FrameSourceException($"Folder frame source: directory not found: '{folder}'.");
        }

        var candidatePaths = Directory
            .EnumerateFiles(folder)
            .Where(HasImageExtension)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        var decodablePaths = new List<string>(candidatePaths.Length);
        var firstWidth = 0;
        var firstHeight = 0;

        foreach (var path in candidatePaths)
        {
            using var probe = Cv2.ImRead(path, ImreadModes.Color);
            if (probe.Empty())
            {
                continue;
            }

            if (decodablePaths.Count == 0)
            {
                firstWidth = probe.Width;
                firstHeight = probe.Height;
            }

            decodablePaths.Add(path);
        }

        if (decodablePaths.Count == 0)
        {
            throw new FrameSourceException($"Folder frame source: '{folder}' contains no decodable image.");
        }

        var directoryName = new DirectoryInfo(folder).Name;
        var geometry = new FrameGeometry(firstWidth, firstHeight, RotationDegrees: 0);

        return new FolderFrameSource(directoryName, decodablePaths.ToArray(), interval, geometry, pool ?? ArrayPool<byte>.Shared);
    }

    /// Newest-frame-only semantics: `ReadAsync` simply drains the channel a
    /// background loop feeds — dropping and disposing stale frames happens
    /// there, not here.
    public async IAsyncEnumerable<CameraFrame> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await foreach (var frame in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return frame;
        }
    }

    /// Stops the timer, stops the production loop, and disposes any frame
    /// still sitting in the channel (one may be, if `ReadAsync` was never
    /// called or stopped before draining it).
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stopCts.Cancel();
        try
        {
            await _produceLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path.
        }

        _timer.Dispose();
        _channel.Writer.TryComplete();

        while (_channel.Reader.TryRead(out var leftover))
        {
            leftover.Dispose();
        }

        _stopCts.Dispose();
    }

    private static void DisposeDroppedFrame(CameraFrame frame) => frame.Dispose();

    private static bool HasImageExtension(string path)
    {
        var extension = Path.GetExtension(path);
        foreach (var candidate in ImageExtensions)
        {
            if (extension.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task ProduceAsync(CancellationToken ct)
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var frame = DecodeNextFrame();

                // Capacity-1 with DropOldest: TryWrite always succeeds — it
                // evicts (and, via itemDropped, disposes) the current
                // occupant rather than rejecting the new item — so there is
                // nothing to retry here.
                _channel.Writer.TryWrite(frame);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path.
        }
        catch (Exception ex)
        {
            var wrapped = ex as FrameSourceException ?? new FrameSourceException("Folder frame source: decode failed.", ex);
            _channel.Writer.TryComplete(wrapped);
            return;
        }

        _channel.Writer.TryComplete();
    }

    private CameraFrame DecodeNextFrame()
    {
        var index = _nextIndex;
        _nextIndex = (_nextIndex + 1) % _imagePaths.Length;

        using var mat = Cv2.ImRead(_imagePaths[index], ImreadModes.Color);
        if (mat.Empty())
        {
            throw new FrameSourceException($"Folder frame source: '{_imagePaths[index]}' failed to decode.");
        }

        var width = mat.Width;
        var height = mat.Height;
        var stride = width * 3; // PixelLayout.Bgr24, packed — our own choice of stride, not the Mat's.
        var length = stride * height;
        var matStep = (int)mat.Step();

        var buffer = _pool.Rent(length);
        for (var row = 0; row < height; row++)
        {
            var srcRow = IntPtr.Add(mat.Data, row * matStep);
            Marshal.Copy(srcRow, buffer, row * stride, stride);
        }

        return new CameraFrame(buffer, width, height, stride, PixelLayout.Bgr24, DateTimeOffset.UtcNow, _pool);
    }
}
