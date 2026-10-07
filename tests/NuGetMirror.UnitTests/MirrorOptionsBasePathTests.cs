using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using NuGetMirror.Configuration;
using NuGetMirror.Proxy;

namespace NuGetMirror.UnitTests;

public sealed class MirrorOptionsBasePathTests
{
    // ── NormalizeBasePath ────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("/", "")]
    [InlineData("nuget", "/nuget")]
    [InlineData("/nuget", "/nuget")]
    [InlineData("/nuget/", "/nuget")]
    [InlineData("nuget/", "/nuget")]
    [InlineData(" /nuget/ ", "/nuget")]   // boundary whitespace + slash — regression for Trim order bug
    [InlineData(" nuget ", "/nuget")]     // boundary whitespace without slash
    [InlineData("feeds/internal", "/feeds/internal")]
    [InlineData("/feeds/internal", "/feeds/internal")]
    [InlineData("/feeds/internal/", "/feeds/internal")]
    [InlineData("feeds/internal/", "/feeds/internal")]
    public void NormalizeBasePath_ReturnsExpected(string? input, string expected)
    {
        string result = MirrorOptions.NormalizeBasePath(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void NormalizedBasePath_ReturnsEmptyString_WhenBasePathIsNull()
    {
        var options = new MirrorOptions { BasePath = null };
        Assert.Equal(string.Empty, options.NormalizedBasePath);
    }

    [Fact]
    public void NormalizedBasePath_ReturnsNormalizedValue()
    {
        var options = new MirrorOptions { BasePath = "/nuget/" };
        Assert.Equal("/nuget", options.NormalizedBasePath);
    }

    // ── ResolveMirrorBase ────────────────────────────────────────────────────

    [Fact]
    public void ResolveMirrorBase_NoPublicBaseUrl_NoBasePath_ReturnsSchemeAndHost()
    {
        HttpContext context = CreateHttpContext("https", "mirror.example.com");

        string result = ProxyHttp.ResolveMirrorBase(context, null, "");

        Assert.Equal("https://mirror.example.com", result);
    }

    [Fact]
    public void ResolveMirrorBase_WithPublicBaseUrl_NoBasePath_ReturnsTrimmedPublicUrl()
    {
        HttpContext context = CreateHttpContext("https", "mirror.example.com");

        string result = ProxyHttp.ResolveMirrorBase(context, "https://public.example.com/", "");

        Assert.Equal("https://public.example.com", result);
    }

    [Fact]
    public void ResolveMirrorBase_NoPublicBaseUrl_WithBasePath_AppendsBasePath()
    {
        HttpContext context = CreateHttpContext("http", "localhost");

        string result = ProxyHttp.ResolveMirrorBase(context, null, "/nuget");

        Assert.Equal("http://localhost/nuget", result);
    }

    [Fact]
    public void ResolveMirrorBase_WithPublicBaseUrl_WithBasePath_AppendsBasePathToPublicUrl()
    {
        HttpContext context = CreateHttpContext("https", "mirror.example.com");

        string result = ProxyHttp.ResolveMirrorBase(context, "https://public.example.com", "/nuget");

        Assert.Equal("https://public.example.com/nuget", result);
    }

    [Fact]
    public void ResolveMirrorBase_WithPublicBaseUrl_WithTrailingSlash_WithBasePath_TrimsSlashThenAppends()
    {
        HttpContext context = CreateHttpContext("https", "mirror.example.com");

        string result = ProxyHttp.ResolveMirrorBase(context, "https://public.example.com/", "/feeds/internal");

        Assert.Equal("https://public.example.com/feeds/internal", result);
    }

    // ── MirrorOptionsBasePathValidator ───────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    [InlineData("nuget")]
    [InlineData("/nuget")]
    [InlineData("/nuget/")]
    [InlineData("/feeds/internal")]
    [InlineData("feeds/internal/")]
    public void Validator_Accepts_ValidValues(string? value)
    {
        var validator = new MirrorOptionsBasePathValidator();
        var options = new MirrorOptions { BasePath = value };

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Succeeded, result.FailureMessage ?? "(no failure message)");
    }

    [Theory]
    [InlineData("../etc/passwd", "traversal")]
    [InlineData("/feeds/../etc", "traversal")]
    [InlineData("/feeds//internal", "consecutive")]
    [InlineData("/nuget?foo=bar", "query")]
    [InlineData("/nuget#anchor", "fragment")]
    [InlineData("feeds:internal", "colon")]
    [InlineData("/feeds/path with spaces", "whitespace")]
    [InlineData("/{tenant}", "brace")]
    [InlineData("/{tenant}/nuget", "brace")]
    [InlineData("/nuget/{id}", "brace")]
    [InlineData("/nuget/*", "wildcard")]
    [InlineData("*", "wildcard")]
    public void Validator_Rejects_InvalidValues(string value, string expectedMessageFragment)
    {
        var validator = new MirrorOptionsBasePathValidator();
        var options = new MirrorOptions { BasePath = value };

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(expectedMessageFragment, result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validator_IsRegistered_And_RejectsInvalidBasePath_AtStartup()
    {
        var services = new ServiceCollection();
        services.AddOptions<MirrorOptions>()
            .Configure(o => o.BasePath = "../etc");
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsValidator>();
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsBasePathValidator>();
        using ServiceProvider sp = services.BuildServiceProvider();

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => sp.GetRequiredService<IOptions<MirrorOptions>>().Value);

        Assert.Contains("BasePath", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static HttpContext CreateHttpContext(string scheme, string host)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString(host);
        return context;
    }
}
