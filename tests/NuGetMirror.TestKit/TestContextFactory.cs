using Microsoft.AspNetCore.Http;

namespace NuGetMirror.TestKit;

public static class TestContextFactory
{
    public static DefaultHttpContext MakeGetContext()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.Method = HttpMethods.Get;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost");
        return context;
    }

    public static DefaultHttpContext MakeHeadContext()
    {
        DefaultHttpContext context = MakeGetContext();
        context.Request.Method = HttpMethods.Head;
        return context;
    }
}
