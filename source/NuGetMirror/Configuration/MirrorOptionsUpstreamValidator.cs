using Microsoft.Extensions.Options;

namespace NuGetMirror.Configuration;

internal sealed class MirrorOptionsUpstreamValidator : IValidateOptions<MirrorOptions>
{
    public ValidateOptionsResult Validate(string? name, MirrorOptions options)
    {
        if (string.IsNullOrEmpty(options.Upstream.IndexUrl))
        {
            return ValidateOptionsResult.Fail(
                "Mirror:Upstream:IndexUrl is required.");
        }

        if (!Uri.TryCreate(options.Upstream.IndexUrl, UriKind.Absolute, out Uri? indexUri)
            || !IsHttpScheme(indexUri))
        {
            return ValidateOptionsResult.Fail(
                $"Mirror:Upstream:IndexUrl must be a valid absolute URL with an http or https scheme. Value: '{options.Upstream.IndexUrl}'.");
        }

        if (!string.IsNullOrEmpty(options.Upstream.Proxy)
            && !Uri.TryCreate(options.Upstream.Proxy, UriKind.Absolute, out _))
        {
            return ValidateOptionsResult.Fail(
                $"Mirror:Upstream:Proxy must be a valid absolute URI. Value: '{options.Upstream.Proxy}'.");
        }

        if (!string.IsNullOrEmpty(options.PublicBaseUrl)
            && (!Uri.TryCreate(options.PublicBaseUrl, UriKind.Absolute, out Uri? publicUri) || !IsHttpScheme(publicUri)))
        {
            return ValidateOptionsResult.Fail(
                $"Mirror:PublicBaseUrl must be a valid absolute URL with an http or https scheme. Value: '{options.PublicBaseUrl}'.");
        }

        return ValidateOptionsResult.Success;
    }

    private static bool IsHttpScheme(Uri uri) => uri.Scheme is "http" or "https";
}
