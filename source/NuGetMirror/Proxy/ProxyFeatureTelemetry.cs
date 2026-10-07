namespace NuGetMirror.Proxy;

internal sealed record ProxyFeatureTelemetry(
    Action? OnRevalidated = null,
    Action? OnStaleFallback = null,
    Action? OnCacheWriteSkipped = null);
