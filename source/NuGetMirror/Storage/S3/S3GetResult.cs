namespace NuGetMirror.Storage.S3;

internal sealed class S3GetResult : IAsyncDisposable, IDisposable
{
    private readonly HttpResponseMessage _response;
    private readonly Stream _stream;

    internal S3GetResult(HttpResponseMessage response, Stream stream, long contentLength, string contentType, string? etag, DateTimeOffset? fetchedAt)
    {
        _response = response;
        _stream = stream;
        ContentLength = contentLength;
        ContentType = contentType;
        ETag = etag;
        FetchedAt = fetchedAt;
    }

    public Stream Stream => _stream;

    public long ContentLength { get; }

    public string ContentType { get; }

    public string? ETag { get; }

    public DateTimeOffset? FetchedAt { get; }

    public void Dispose()
    {
        _stream.Dispose();
        _response.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync().ConfigureAwait(false);
        _response.Dispose();
    }
}
