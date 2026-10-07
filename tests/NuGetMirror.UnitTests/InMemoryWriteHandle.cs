using NuGetMirror.Storage;

namespace NuGetMirror.UnitTests;

internal sealed class InMemoryWriteHandle : ICacheWriteHandle
{
    private readonly MemoryStream _inner = new();
    private readonly Stream _stream;
    private readonly Action<int> _onCommit;
    private readonly Action? _onDispose;

    public InMemoryWriteHandle(Action<int> onCommit, long throwsAfterBytes = 0, Action? onDispose = null)
    {
        _onCommit = onCommit;
        _onDispose = onDispose;
        _stream = throwsAfterBytes > 0
            ? new ThrowingAfterStream(_inner, throwsAfterBytes)
            : _inner;
    }

    public Stream Stream => _stream;

    public void SetMetadata(string contentType, long length, string? etag) { }

    public ValueTask CommitAsync(CancellationToken ct)
    {
        _onCommit((int)_inner.Length);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _onDispose?.Invoke();

        if (_stream is ThrowingAfterStream tas)
        {
            await tas.DisposeAsync();
        }
        else
        {
            await _inner.DisposeAsync();
        }
    }

    /// <summary>
    /// A write-only stream wrapper that throws <see cref="IOException"/> once the
    /// total number of bytes written exceeds <paramref name="threshold"/>.  Used to
    /// simulate a mid-stream cache write failure (e.g. disk full or storage error).
    /// </summary>
    private sealed class ThrowingAfterStream(Stream inner, long threshold) : Stream
    {
        private long _written;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            _written += count;
            if (_written > threshold)
            {
                throw new IOException("Simulated cache write failure after threshold.");
            }

            inner.Write(buffer, offset, count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _written += buffer.Length;
            if (_written > threshold)
            {
                throw new IOException("Simulated cache write failure after threshold.");
            }

            await inner.WriteAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
