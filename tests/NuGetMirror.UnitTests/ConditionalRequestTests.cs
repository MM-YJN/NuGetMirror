using Microsoft.AspNetCore.Http;

using NuGetMirror.Proxy;

namespace NuGetMirror.UnitTests;

public sealed class ConditionalRequestTests
{
    [Fact]
    public void TryWriteNotModified_ReturnsFalse_WhenEtagNull()
    {
        DefaultHttpContext context = CreateContext(null);

        bool result = ConditionalRequest.TryWriteNotModified(context, null);

        Assert.False(result);
        Assert.NotEqual(StatusCodes.Status304NotModified, context.Response.StatusCode);
    }

    [Fact]
    public void TryWriteNotModified_ReturnsFalse_WhenNoIfNoneMatchHeader()
    {
        DefaultHttpContext context = CreateContext(null);

        bool result = ConditionalRequest.TryWriteNotModified(context, "\"abc123\"");

        Assert.False(result);
        Assert.NotEqual(StatusCodes.Status304NotModified, context.Response.StatusCode);
    }

    [Fact]
    public void TryWriteNotModified_ReturnsTrue_WhenExactMatch()
    {
        DefaultHttpContext context = CreateContext("\"abc123\"");

        bool result = ConditionalRequest.TryWriteNotModified(context, "\"abc123\"");

        Assert.True(result);
        Assert.Equal(StatusCodes.Status304NotModified, context.Response.StatusCode);
        Assert.Equal("\"abc123\"", context.Response.Headers.ETag);
    }

    [Fact]
    public void TryWriteNotModified_ReturnsTrue_WhenWeakMatch()
    {
        DefaultHttpContext context = CreateContext("W/\"abc123\"");

        bool result = ConditionalRequest.TryWriteNotModified(context, "\"abc123\"");

        Assert.True(result);
        Assert.Equal(StatusCodes.Status304NotModified, context.Response.StatusCode);
        Assert.Equal("\"abc123\"", context.Response.Headers.ETag);
    }

    [Fact]
    public void TryWriteNotModified_ReturnsTrue_WhenStoredIsWeak()
    {
        DefaultHttpContext context = CreateContext("\"abc123\"");

        bool result = ConditionalRequest.TryWriteNotModified(context, "W/\"abc123\"");

        Assert.True(result);
        Assert.Equal(StatusCodes.Status304NotModified, context.Response.StatusCode);
        Assert.Equal("W/\"abc123\"", context.Response.Headers.ETag);
    }

    [Fact]
    public void TryWriteNotModified_ReturnsTrue_WhenWildcard()
    {
        DefaultHttpContext context = CreateContext("*");

        bool result = ConditionalRequest.TryWriteNotModified(context, "\"anytag\"");

        Assert.True(result);
    }

    [Fact]
    public void TryWriteNotModified_ReturnsTrue_WhenMultiValueWithMatch()
    {
        DefaultHttpContext context = CreateContext("\"old\", \"abc123\", \"other\"");

        bool result = ConditionalRequest.TryWriteNotModified(context, "\"abc123\"");

        Assert.True(result);
    }

    [Fact]
    public void TryWriteNotModified_ReturnsTrue_WhenMultiValueWeakWithMatch()
    {
        DefaultHttpContext context = CreateContext("W/\"old\", W/\"abc123\"");

        bool result = ConditionalRequest.TryWriteNotModified(context, "W/\"abc123\"");

        Assert.True(result);
    }

    [Fact]
    public void TryWriteNotModified_ReturnsFalse_WhenNoMatch()
    {
        DefaultHttpContext context = CreateContext("\"xyz\"");

        bool result = ConditionalRequest.TryWriteNotModified(context, "\"abc123\"");

        Assert.False(result);
    }

    [Fact]
    public void TryWriteNotModified_ReturnsFalse_WhenMultiValueNoMatch()
    {
        DefaultHttpContext context = CreateContext("\"old\", \"other\"");

        bool result = ConditionalRequest.TryWriteNotModified(context, "\"abc123\"");

        Assert.False(result);
    }

    [Fact]
    public void TryWriteNotModified_ReturnsTrue_WhenWildcardInMultiValue()
    {
        DefaultHttpContext context = CreateContext("\"old\", *, \"other\"");

        bool result = ConditionalRequest.TryWriteNotModified(context, "\"anything\"");

        Assert.True(result);
    }

    [Fact]
    public void TryWriteNotModified_ReturnsFalse_WhenHeaderIsEmpty()
    {
        DefaultHttpContext context = CreateContext("");

        bool result = ConditionalRequest.TryWriteNotModified(context, "\"abc123\"");

        Assert.False(result);
    }

    [Fact]
    public void TryWriteNotModified_ReturnsFalse_WhenHeaderIsMalformed()
    {
        DefaultHttpContext context = CreateContext("garbage");

        bool result = ConditionalRequest.TryWriteNotModified(context, "\"abc123\"");

        Assert.False(result);
    }

    [Fact]
    public void TryWriteNotModified_ReturnsTrue_WhenMatchInSecondHeaderValue()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.IfNoneMatch = "\"old\"";
        context.Request.Headers.Append("If-None-Match", "\"abc123\"");

        bool result = ConditionalRequest.TryWriteNotModified(context, "\"abc123\"");

        Assert.True(result);
        Assert.Equal(StatusCodes.Status304NotModified, context.Response.StatusCode);
        Assert.Equal("\"abc123\"", context.Response.Headers.ETag);
    }

    [Fact]
    public void TryWriteNotModified_ReturnsFalse_WhenNoMatchAcrossHeaderValues()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.IfNoneMatch = "\"old\"";
        context.Request.Headers.Append("If-None-Match", "\"other\"");

        bool result = ConditionalRequest.TryWriteNotModified(context, "\"abc123\"");

        Assert.False(result);
    }

    [Fact]
    public void TryWriteNotModified_ReturnsTrue_WhenWildcardInSecondHeaderValue()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.IfNoneMatch = "\"old\"";
        context.Request.Headers.Append("If-None-Match", "*");

        bool result = ConditionalRequest.TryWriteNotModified(context, "\"anything\"");

        Assert.True(result);
        Assert.Equal(StatusCodes.Status304NotModified, context.Response.StatusCode);
        Assert.Equal("\"anything\"", context.Response.Headers.ETag);
    }

    [Fact]
    public void TryWriteNotModified_ReturnsFalse_WhenEtagHasUnclosedStrongQuote()
    {
        DefaultHttpContext context = CreateContext("\"abc");

        bool result = ConditionalRequest.TryWriteNotModified(context, "\"abc123\"");

        Assert.False(result);
    }

    [Fact]
    public void TryWriteNotModified_ReturnsFalse_WhenWeakEtagHasUnclosedQuote()
    {
        DefaultHttpContext context = CreateContext("W/\"abc");

        bool result = ConditionalRequest.TryWriteNotModified(context, "\"abc123\"");

        Assert.False(result);
    }

    [Fact]
    public void TryWriteNotModified_ReturnsFalse_WhenWeakPrefixNotFollowedByQuote()
    {
        DefaultHttpContext context = CreateContext("W/abc");

        bool result = ConditionalRequest.TryWriteNotModified(context, "\"abc123\"");

        Assert.False(result);
    }

    [Fact]
    public void TryWriteNotModified_ReturnsFalse_WhenWeakPrefixFollowedByOnlyOneChar()
    {
        DefaultHttpContext context = CreateContext("W/\"");

        bool result = ConditionalRequest.TryWriteNotModified(context, "\"abc123\"");

        Assert.False(result);
    }

    [Fact]
    public void TryWriteNotModified_ReturnsTrue_WhenWildcardMatchesEvenWithMalformedStoredEtag()
    {
        DefaultHttpContext context = CreateContext("*");

        bool result = ConditionalRequest.TryWriteNotModified(context, "garbage");

        Assert.True(result);
        Assert.Equal(StatusCodes.Status304NotModified, context.Response.StatusCode);
        Assert.Equal("garbage", context.Response.Headers.ETag);
    }

    private static DefaultHttpContext CreateContext(string? ifNoneMatch)
    {
        var context = new DefaultHttpContext();

        if (ifNoneMatch is not null)
        {
            context.Request.Headers.IfNoneMatch = ifNoneMatch;
        }

        return context;
    }
}
