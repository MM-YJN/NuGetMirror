using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Http;

using NuGetMirror.Diagnostics;
using NuGetMirror.Json;
using NuGetMirror.Storage;

using Polly;

namespace NuGetMirror.Proxy;

internal static class ProxyHttp
{
    internal const int StreamCopyBufferSize = 65536;

    internal static string MethodTag(HttpContext context)
    {
        string method = context.Request.Method;
        if (HttpMethods.IsGet(method))
        {
            return "get";
        }

        if (HttpMethods.IsHead(method))
        {
            return "head";
        }

        // HTTP method tokens are uppercase per RFC 7230; GET and HEAD are the only
        // methods that reach the cache pipeline, so this branch is dead on the hot path.
        return method.ToLowerInvariant();
    }

    internal static async Task WriteProblemAsync(HttpContext context, int statusCode, string detail)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";

        var problem = new ProblemDetails
        {
            Type = "about:blank",
            Title = ((HttpStatusCode)statusCode).ToString(),
            Status = statusCode,
            Detail = detail,
        };

        string json = JsonSerializer.Serialize(problem, MirrorJsonContext.Default.ProblemDetails);
        await context.Response.WriteAsync(json, context.RequestAborted).ConfigureAwait(false);
    }

    internal static ValueTask<long> WriteJsonResponseAsync(HttpContext context, HttpStatusCode statusCode, string body, CancellationToken ct)
    {
        context.Response.StatusCode = (int)statusCode;
        context.Response.ContentType = "application/json";
        return WriteBodyUtf8Async(context, body, ct);
    }

    /// <summary>
    /// Sets <c>Content-Length</c>, writes the body to the response, and returns the byte count.
    /// Does not set status code or content type.
    /// </summary>
    internal static async ValueTask<long> WriteBodyUtf8Async(HttpContext context, string body, CancellationToken ct)
    {
        // Two-pass UTF-8: GetByteCount sets Content-Length before any bytes are flushed,
        // then WriteAsync encodes directly into the response pipe's IBufferWriter<byte>
        // segments — no intermediate byte[] allocation.
        int byteCount = Encoding.UTF8.GetByteCount(body);
        if (context.Response.StatusCode != StatusCodes.Status204NoContent)
        {
            context.Response.ContentLength = byteCount;
        }

        await context.Response.WriteAsync(body, ct).ConfigureAwait(false);
        return byteCount;
    }

    internal static string ResolveMirrorBase(HttpContext context, string? publicBaseUrl, string basePath = "")
    {
        string origin = !string.IsNullOrEmpty(publicBaseUrl)
            ? publicBaseUrl.TrimEnd('/')
            : $"{context.Request.Scheme}://{context.Request.Host}";

        // basePath is already normalized (empty string or "/segment"), so appending directly
        // produces the correct mirror base (e.g. "https://host" + "/nuget" = "https://host/nuget").
        return origin + basePath;
    }

    internal static Uri ComposeUpstreamUri(string upstreamBase, string? remainingPath, string? queryString)
    {
        if (remainingPath is not null && remainingPath.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Path traversal sequences ('..') are not allowed.", nameof(remainingPath));
        }

        string path = upstreamBase + (remainingPath ?? "");

        if (!string.IsNullOrEmpty(queryString))
        {
            path += queryString;
        }

        return new Uri(path);
    }

    internal static void SetCachedContentHeaders(HttpContext context, CachedContent cached)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = cached.ContentType;
        context.Response.ContentLength = cached.Length;

        if (cached.ETag is not null)
        {
            context.Response.Headers.ETag = cached.ETag;
        }
    }

    internal static void CopyStreamHeaders(HttpContext context, HttpResponseMessage response)
    {
        HttpContentHeaders srcContentHeaders = response.Content.Headers;

        if (srcContentHeaders.ContentType is not null)
        {
            context.Response.ContentType = srcContentHeaders.ContentType.ToString();
        }

        if (srcContentHeaders.ContentLength.HasValue)
        {
            context.Response.ContentLength = srcContentHeaders.ContentLength.Value;
        }

        if (srcContentHeaders.LastModified.HasValue)
        {
            context.Response.Headers.Append("Last-Modified", srcContentHeaders.LastModified.Value.ToString("R"));
        }

        foreach ((string? key, IEnumerable<string>? values) in response.Headers)
        {
            if (key is "ETag" or "Accept-Ranges")
            {
                foreach (string value in values)
                {
                    context.Response.Headers.Append(key, value);
                }
            }
        }
    }

    internal static async Task StreamResponseWithoutCachingAsync(HttpContext context, HttpResponseMessage response, CancellationToken ct)
    {
        context.Response.StatusCode = (int)response.StatusCode;

        CopyStreamHeaders(context, response);

        string? requestMethod = response.RequestMessage?.Method?.Method;

        if (!HttpMethods.IsHead(context.Request.Method) && (requestMethod is null || !HttpMethods.IsHead(requestMethod)))
        {
            await response.Content.CopyToAsync(context.Response.Body, ct).ConfigureAwait(false);
        }
    }

    internal static async Task ExecuteUpstreamFetchAsync(
        HttpContext context,
        MirrorMetrics metrics,
        string cancellationStage,
        Func<Task> fetch,
        Func<Exception, UpstreamFailureKind, Task> onFailure,
        bool handleUnexpected)
    {
        try
        {
            await fetch().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException { InnerException: TaskCanceledException })
        {
            if (context.RequestAborted.IsCancellationRequested)
            {
                metrics.RecordClientCancellation(cancellationStage);
            }
        }
        catch (HttpRequestException ex)
        {
            await onFailure(ex, UpstreamFailureKind.Upstream).ConfigureAwait(false);
        }
        catch (ExecutionRejectedException ex)
        {
            await onFailure(ex, UpstreamFailureKind.ResilienceRejected).ConfigureAwait(false);
        }
        catch (Exception ex) when (handleUnexpected)
        {
            await onFailure(ex, UpstreamFailureKind.Unexpected).ConfigureAwait(false);
        }
    }

    internal static async Task WriteUpstreamErrorAsync(HttpContext context, MirrorMetrics metrics, Exception ex)
    {
        if (!context.Response.HasStarted)
        {
            metrics.RecordProxyError("upstream", StatusCodes.Status502BadGateway);
            await WriteProblemAsync(context, StatusCodes.Status502BadGateway, ex.Message).ConfigureAwait(false);
        }
    }
}
