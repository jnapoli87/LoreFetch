namespace LoreFetch.Tests.StreamB.Images;

/// Delivers up to `failAfterBytes` bytes from `data`, then throws
/// `IOException` on the next read -- simulating a connection dropping
/// mid-download, the exact scenario the temp-file-then-move pattern
/// exists to survive.
internal sealed class FaultyStream : Stream
{
    private readonly byte[] _data;
    private readonly int _failAfterBytes;
    private int _delivered;

    public FaultyStream(byte[] data, int failAfterBytes)
    {
        _data = data;
        _failAfterBytes = failAfterBytes;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Yield();

        if (_delivered >= _failAfterBytes)
        {
            throw new IOException("simulated connection drop mid-download");
        }

        var toCopy = new[] { buffer.Length, _failAfterBytes - _delivered, _data.Length - _delivered }.Min();
        if (toCopy <= 0)
        {
            return 0;
        }

        _data.AsSpan(_delivered, toCopy).CopyTo(buffer.Span);
        _delivered += toCopy;
        return toCopy;
    }

    public override void Flush() => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
