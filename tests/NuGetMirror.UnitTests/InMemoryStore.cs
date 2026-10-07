using System.Runtime.CompilerServices;

using NuGetMirror.Storage;

namespace NuGetMirror.UnitTests;

internal sealed class InMemoryStore : IPackageContentStore, ICacheMaintenance, ICacheRevalidation
{
    public byte[]? CachedBytes { get; init; }
    public Stream? CachedStream { get; init; }
    public long Length { get; init; }
    public string ContentType { get; init; } = "application/octet-stream";
    public DateTimeOffset? StoredAt { get; init; }
    public string? ETag { get; init; }
    public bool WithMaintenance { get; init; }
    public bool WithRevalidation { get; init; }
    public bool ReturnsNull { get; init; }
    public bool BeginWriteThrows { get; init; }
    public long WriteHandleThrowsAfterBytes { get; init; }
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    public bool TouchCalled { get; private set; }
    public List<string> TouchedKeys { get; } = [];
    public bool RevalidationCalled { get; private set; }
    public bool WasWritten { get; private set; }
    public string? LastWrittenKey { get; private set; }
    public int WriteHandleDisposeCount { get; private set; }

    public ValueTask<CachedContent?> TryGetAsync(string key, CancellationToken ct)
    {
        if (ReturnsNull || (CachedBytes is null && CachedStream is null))
        {
            return ValueTask.FromResult<CachedContent?>(null);
        }

        Stream stream = CachedStream ?? new MemoryStream(CachedBytes!);
        return ValueTask.FromResult<CachedContent?>(new CachedContent
        {
            Stream = stream,
            Length = Length > 0 ? Length : CachedBytes?.Length ?? 0,
            ContentType = ContentType,
            ETag = ETag,
            StoredAtUtc = StoredAt ?? TimeProvider.GetUtcNow(),
        });
    }

    public ValueTask<ICacheWriteHandle> BeginWriteAsync(string key, CancellationToken ct)
    {
        LastWrittenKey = key;
        if (BeginWriteThrows)
        {
            throw new IOException("Simulated BeginWriteAsync failure.");
        }

        return ValueTask.FromResult<ICacheWriteHandle>(
            new InMemoryWriteHandle(bytes => WasWritten = bytes > 0, WriteHandleThrowsAfterBytes, () => WriteHandleDisposeCount++));
    }

    public async IAsyncEnumerable<CacheEntryInfo> EnumerateAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        yield break;
    }

    public ValueTask DeleteAsync(string key, CancellationToken ct) => ValueTask.CompletedTask;

    public ValueTask TouchAsync(string key, CancellationToken ct)
    {
        TouchCalled = true;
        TouchedKeys.Add(key);
        return ValueTask.CompletedTask;
    }

    public ValueTask RefreshTimestampAsync(string key, string contentType, string? etag, DateTimeOffset fetchedAtUtc, CancellationToken ct)
    {
        RevalidationCalled = true;
        return ValueTask.CompletedTask;
    }
}
