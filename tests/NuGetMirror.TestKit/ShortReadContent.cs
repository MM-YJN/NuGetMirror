using System.Net;
using System.Net.Http.Headers;

namespace NuGetMirror.TestKit;

/// <summary>
/// An <see cref="HttpContent"/> that reports a <c>Content-Length</c> larger than
/// the actual body bytes. Used to simulate an upstream response where the stream
/// ends prematurely (e.g. connection drop mid-transfer).
/// </summary>
internal sealed class ShortReadContent(byte[] actualBytes, long declaredLength) : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => SerializeToStreamAsync(stream);

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        => SerializeToStreamAsync(stream, cancellationToken);

    private Task SerializeToStreamAsync(Stream stream, CancellationToken ct = default)
        => stream.WriteAsync(actualBytes, ct).AsTask();

    protected override bool TryComputeLength(out long length)
    {
        length = declaredLength;
        return true;
    }

    protected override Task<Stream> CreateContentReadStreamAsync()
        => Task.FromResult<Stream>(new MemoryStream(actualBytes));

    protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        return new MemoryStream(actualBytes);
    }
}
