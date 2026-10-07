using NuGetMirror.Storage;

namespace NuGetMirror.UnitTests;

internal sealed class DelayedHitStore(byte[] cachedBytes, int hitOnAttempt, TimeProvider? timeProvider = null) : IPackageContentStore
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private int _attempt;

    public ValueTask<CachedContent?> TryGetAsync(string key, CancellationToken ct)
    {
        _attempt++;
        if (_attempt >= hitOnAttempt)
        {
            return ValueTask.FromResult<CachedContent?>(new CachedContent
            {
                Stream = new MemoryStream(cachedBytes),
                Length = cachedBytes.Length,
                ContentType = "application/octet-stream",
                StoredAtUtc = _timeProvider.GetUtcNow(),
            });
        }

        return ValueTask.FromResult<CachedContent?>(null);
    }

    public ValueTask<ICacheWriteHandle> BeginWriteAsync(string key, CancellationToken ct)
        => throw new NotSupportedException();
}
