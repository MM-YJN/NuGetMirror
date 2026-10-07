using System.Net;

using NuGetMirror.Configuration;

namespace NuGetMirror.Upstream;

internal static class UpstreamHandlerFactory
{
    internal const int DefaultMaxConnectionsPerServer = 50;
    internal static readonly TimeSpan s_defaultPooledConnectionLifetime = TimeSpan.FromMinutes(2);

    public static SocketsHttpHandler CreateHandler(UpstreamOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = s_defaultPooledConnectionLifetime,
            MaxConnectionsPerServer = DefaultMaxConnectionsPerServer,
            ConnectTimeout = options.Resilience.ConnectTimeout,
        };

        if (!string.IsNullOrEmpty(options.Proxy))
        {
            var proxyUri = new Uri(options.Proxy);
            var webProxy = new WebProxy(proxyUri);

            if (!string.IsNullOrEmpty(proxyUri.UserInfo))
            {
                webProxy.Credentials = ParseProxyCredentials(proxyUri.UserInfo);
            }

            handler.Proxy = webProxy;
            handler.UseProxy = true;
        }

        return handler;
    }

    private static NetworkCredential ParseProxyCredentials(ReadOnlySpan<char> userInfo)
    {
        Span<Range> parts = stackalloc Range[2];
        int numberOfRanges = userInfo.Split(parts, ':');
        ReadOnlySpan<char> username = userInfo[parts[0]];
        ReadOnlySpan<char> password = numberOfRanges > 1 ? userInfo[parts[1]] : default;
        return new NetworkCredential(username.ToString(), password.ToString());
    }
}
