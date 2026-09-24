using System.Buffers;

namespace LoreFetch.Tests.Capture;

/// Wraps `ArrayPool&lt;byte&gt;.Shared` and counts rents, returns and the
/// peak number of buffers outstanding at once, so tests can assert "every
/// rented buffer comes back" and "memory stays bounded" without inspecting
/// the shared pool's private state or relying on GC timing.
internal sealed class CountingArrayPool : ArrayPool<byte>
{
    private long _rentCount;
    private long _returnCount;
    private long _outstanding;
    private long _peakOutstanding;

    internal long RentCount => Interlocked.Read(ref _rentCount);

    internal long ReturnCount => Interlocked.Read(ref _returnCount);

    /// The largest number of buffers ever rented-but-not-yet-returned at any
    /// single instant. A channel that queues instead of dropping shows up
    /// here as a value that grows with the number of pushes; a bounded
    /// newest-frame-only channel keeps this at a small constant regardless.
    internal long PeakOutstanding => Interlocked.Read(ref _peakOutstanding);

    public override byte[] Rent(int minimumLength)
    {
        Interlocked.Increment(ref _rentCount);
        var outstanding = Interlocked.Increment(ref _outstanding);
        RaisePeak(outstanding);
        return Shared.Rent(minimumLength);
    }

    public override void Return(byte[] array, bool clearArray = false)
    {
        Interlocked.Increment(ref _returnCount);
        Interlocked.Decrement(ref _outstanding);
        Shared.Return(array, clearArray);
    }

    private void RaisePeak(long candidate)
    {
        long current;
        do
        {
            current = Interlocked.Read(ref _peakOutstanding);
            if (candidate <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _peakOutstanding, candidate, current) != current);
    }
}
