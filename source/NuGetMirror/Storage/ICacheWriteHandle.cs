namespace NuGetMirror.Storage;

internal interface ICacheWriteHandle : IAsyncDisposable
{
    Stream Stream { get; }

    void SetMetadata(string contentType, long length, string? etag);

    ValueTask CommitAsync(CancellationToken ct);
}
