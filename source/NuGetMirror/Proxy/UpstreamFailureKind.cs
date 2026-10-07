namespace NuGetMirror.Proxy;

internal enum UpstreamFailureKind
{
    Upstream,
    ResilienceRejected,
    Unexpected,
}
