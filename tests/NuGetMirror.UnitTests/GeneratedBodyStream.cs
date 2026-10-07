namespace NuGetMirror.UnitTests;

/// <summary>Generates bytes without retaining a payload; exposes consumption and ownership.</summary>
internal sealed class GeneratedBodyStream(long length) : Stream
{
    public long BytesRead { get; private set; }
    public bool Disposed { get; private set; }
    public Exception? ReadFailure { get; init; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        if (ReadFailure is { } failure)
        {
            throw failure;
        }

        int count = (int)Math.Min(buffer.Length, length - BytesRead);
        buffer.Span[..count].Fill((byte)' ');
        BytesRead += count;
        return count;
    }

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
