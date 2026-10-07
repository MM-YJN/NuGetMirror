namespace NuGetMirror.TestKit;

public sealed class GenericThrowingHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Simulated non-HTTP upstream failure");
}
