namespace NuGetMirror.Storage;

internal interface ICacheRevalidation
{
    ValueTask RefreshTimestampAsync(string key, string contentType, string? etag, DateTimeOffset fetchedAtUtc, CancellationToken ct);
}
