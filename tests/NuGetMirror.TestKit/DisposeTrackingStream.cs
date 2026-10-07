namespace NuGetMirror.TestKit;

public sealed class DisposeTrackingStream(Stream inner) : Stream
{
    private bool _disposed;

    public bool DisposeCalled => _disposed;
    public bool DisposeAsyncCalled { get; private set; }

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => inner.ReadAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask DisposeAsync()
    {
        DisposeAsyncCalled = true;
        await inner.DisposeAsync();
        await base.DisposeAsync();
    }

    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
