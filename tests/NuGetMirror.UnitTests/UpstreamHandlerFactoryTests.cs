using System.Net;

using NuGetMirror.Configuration;
using NuGetMirror.Upstream;

namespace NuGetMirror.UnitTests;

public sealed class UpstreamHandlerFactoryTests
{
    [Fact]
    public void CreateHandler_SetsProxyAndUseProxy_WhenProxyUrlIsConfigured()
    {
        var options = new UpstreamOptions { Proxy = "http://proxy.example.com:8080" };

        SocketsHttpHandler handler = UpstreamHandlerFactory.CreateHandler(options);

        Assert.True(handler.UseProxy);
        Assert.NotNull(handler.Proxy);
        WebProxy webProxy = Assert.IsType<WebProxy>(handler.Proxy);
        Assert.Equal("http://proxy.example.com:8080/", webProxy.Address?.AbsoluteUri);
    }

    [Fact]
    public void CreateHandler_SetsProxyCredentials_WhenProxyUrlContainsUserInfo()
    {
        var options = new UpstreamOptions { Proxy = "http://user:pass@proxy.example.com:8080" };

        SocketsHttpHandler handler = UpstreamHandlerFactory.CreateHandler(options);

        WebProxy webProxy = Assert.IsType<WebProxy>(handler.Proxy);
        Assert.NotNull(webProxy.Credentials);
        NetworkCredential creds = Assert.IsType<NetworkCredential>(webProxy.Credentials);
        Assert.Equal("user", creds.UserName);
        Assert.Equal("pass", creds.Password);
    }

    [Fact]
    public void CreateHandler_SetsProxyCredentials_WhenProxyUrlContainsUsernameOnly()
    {
        var options = new UpstreamOptions { Proxy = "http://user@proxy.example.com:8080" };

        SocketsHttpHandler handler = UpstreamHandlerFactory.CreateHandler(options);

        WebProxy webProxy = Assert.IsType<WebProxy>(handler.Proxy);
        Assert.NotNull(webProxy.Credentials);
        NetworkCredential creds = Assert.IsType<NetworkCredential>(webProxy.Credentials);
        Assert.Equal("user", creds.UserName);
        Assert.Equal(string.Empty, creds.Password);
    }

    [Fact]
    public void CreateHandler_DoesNotSetCredentials_WhenProxyUrlHasNoUserInfo()
    {
        var options = new UpstreamOptions { Proxy = "http://proxy.example.com:8080" };

        SocketsHttpHandler handler = UpstreamHandlerFactory.CreateHandler(options);

        WebProxy webProxy = Assert.IsType<WebProxy>(handler.Proxy);
        Assert.Null(webProxy.Credentials);
    }

    [Fact]
    public void CreateHandler_DoesNotSetWebProxy_WhenProxyUrlIsNull()
    {
        var options = new UpstreamOptions { Proxy = null };

        SocketsHttpHandler handler = UpstreamHandlerFactory.CreateHandler(options);

        Assert.Null(handler.Proxy);
    }

    [Fact]
    public void CreateHandler_DoesNotSetWebProxy_WhenProxyUrlIsEmpty()
    {
        var options = new UpstreamOptions { Proxy = string.Empty };

        SocketsHttpHandler handler = UpstreamHandlerFactory.CreateHandler(options);

        Assert.Null(handler.Proxy);
    }

    [Fact]
    public void CreateHandler_SetsDecompressionAndConnectionSettings()
    {
        var options = new UpstreamOptions();

        SocketsHttpHandler handler = UpstreamHandlerFactory.CreateHandler(options);

        Assert.Equal(DecompressionMethods.All, handler.AutomaticDecompression);
        Assert.Equal(TimeSpan.FromMinutes(2), handler.PooledConnectionLifetime);
        Assert.Equal(50, handler.MaxConnectionsPerServer);
    }
}
