using System.Buffers;
using LoreFetch.Core.Abstractions;
using Xunit;

namespace LoreFetch.Tests.Integration.Unit;

/// A test `ArrayPool<byte>` that tallies every Rent and Return, so
/// "the buffer was returned exactly once" can be asserted directly rather
/// than inferred.
///
/// Thread-safe on purpose: `FolderFrameSource` rents on its background
/// producer loop while frames are returned on the consumer's thread or a
/// channel drop, and the tests read the counts while that loop runs. A plain
/// `++` can lose an increment to that race, which makes RentCount and
/// ReturnCount disagree at random.
public sealed class CountingArrayPool : ArrayPool<byte>
{
    private int _rentCount;
    private int _returnCount;

    public int RentCount => Volatile.Read(ref _rentCount);
    public int ReturnCount => Volatile.Read(ref _returnCount);

    public override byte[] Rent(int minimumLength)
    {
        Interlocked.Increment(ref _rentCount);
        return Shared.Rent(minimumLength);
    }

    public override void Return(byte[] array, bool clearArray = false)
    {
        Interlocked.Increment(ref _returnCount);
        Shared.Return(array, clearArray);
    }
}

public class CameraFrameTests
{
    [Fact]
    public void Dispose_TwiceReturnsBufferExactlyOnce()
    {
        var pool = new CountingArrayPool();
        var buffer = pool.Rent(64);

        var frame = new CameraFrame(
            buffer,
            width: 8,
            height: 4,
            stride: 8,
            layout: PixelLayout.Bgr24,
            capturedAt: DateTimeOffset.UtcNow,
            pool: pool);

        frame.Dispose();
        frame.Dispose();
        frame.Dispose();

        Assert.Equal(1, pool.ReturnCount);
    }

    [Fact]
    public void Pixels_LengthIsExactlyStrideTimesHeight_WhenPoolBufferIsLonger()
    {
        var pool = new CountingArrayPool();
        // Request a buffer far larger than stride*height so ArrayPool's
        // round-up behaviour is exercised for real, not merely assumed.
        var buffer = pool.Rent(1_000_000);

        const int stride = 8;
        const int height = 4;

        var frame = new CameraFrame(
            buffer,
            width: 8,
            height: height,
            stride: stride,
            layout: PixelLayout.Bgr24,
            capturedAt: DateTimeOffset.UtcNow,
            pool: pool);

        Assert.Equal(stride * height, frame.Pixels.Length);
        Assert.True(buffer.Length > frame.Pixels.Length);

        frame.Dispose();
    }

    [Fact]
    public void Dispose_WithNullPool_ReturnsNothingAnywhere()
    {
        var buffer = new byte[32];

        var frame = new CameraFrame(
            buffer,
            width: 8,
            height: 4,
            stride: 8,
            layout: PixelLayout.Bgr24,
            capturedAt: DateTimeOffset.UtcNow,
            pool: null);

        // No pool was given, so there is nothing to return to, and disposing
        // (repeatedly) must not throw.
        frame.Dispose();
        frame.Dispose();
    }
}
