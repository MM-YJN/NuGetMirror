using System.Buffers;

using NuGetMirror.Diagnostics;
using NuGetMirror.Upstream;

namespace NuGetMirror.Proxy;

internal static partial class ProxyStreaming
{
    internal static async Task<(long Copied, bool CacheOk)> TeeStreamAsync(Stream source, Stream clientOutput, Stream cacheOutput, string cacheKey, ILogger logger, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ProxyHttp.StreamCopyBufferSize);
        long totalCopied = 0;
        bool cacheOk = true;

        try
        {
            while (true)
            {
                int read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                await clientOutput.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);

                if (cacheOk)
                {
                    try
                    {
                        await cacheOutput.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        LogTeeCacheFailed(logger, ex, cacheKey);
                        cacheOk = false;
                    }
                }

                totalCopied += read;
            }

            await clientOutput.FlushAsync(ct).ConfigureAwait(false);

            if (cacheOk)
            {
                try
                {
                    await cacheOutput.FlushAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogTeeCacheFailed(logger, ex, cacheKey);
                    cacheOk = false;
                }
            }

            return (totalCopied, cacheOk);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Reads the upstream response body up to <paramref name="maxBytes"/>, returning a new exact-size
    /// <see cref="byte"/>[] containing the body data.
    /// </summary>
    internal static async Task<byte[]?> ReadResponseBodyWithLimitAsync(
        HttpContext context, HttpResponseMessage response, long maxBytes,
        string errorMessage, MirrorMetrics metrics, CancellationToken ct)
    {
        byte[]? body = await BoundedResponseReader.ReadAsync(response.Content, maxBytes, ct).ConfigureAwait(false);
        if (body is null)
        {
            metrics.RecordProxyError("too_large", StatusCodes.Status502BadGateway);
            await ProxyHttp.WriteProblemAsync(context, StatusCodes.Status502BadGateway, errorMessage).ConfigureAwait(false);
        }

        return body;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cache write failed during streaming tee for {CacheKey}.")]
    private static partial void LogTeeCacheFailed(ILogger logger, Exception ex, string cacheKey);
}
