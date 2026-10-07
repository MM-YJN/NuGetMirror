using System.Net;

namespace NuGetMirror.UnitTests;

internal sealed class GeneratedBodyContent(GeneratedBodyStream stream) : HttpContent
{
    public int StreamsOpened { get; private set; }

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }

    protected override Task SerializeToStreamAsync(Stream target, TransportContext? context)
        => throw new InvalidOperationException("Content must not be buffered by HttpClient.");

    protected override Task<Stream> CreateContentReadStreamAsync()
    {
        StreamsOpened++;
        return Task.FromResult<Stream>(stream);
    }

    protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        => CreateContentReadStreamAsync();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            stream.Dispose();
        }

        base.Dispose(disposing);
    }
}
