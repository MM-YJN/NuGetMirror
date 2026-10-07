namespace NuGetMirror.Storage;

internal interface IPackageContentStore
{
    ValueTask<CachedContent?> TryGetAsync(string key, CancellationToken ct);

    ValueTask<ICacheWriteHandle> BeginWriteAsync(string key, CancellationToken ct);
}
