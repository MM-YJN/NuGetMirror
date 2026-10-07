namespace NuGetMirror.Storage;

internal interface IStorageHealthProbe
{
    ValueTask CheckAsync(CancellationToken ct);
}
