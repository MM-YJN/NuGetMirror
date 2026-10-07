using System.Buffers;

namespace NuGetMirror.Upstream;

/// <summary>Bounds accepted body bytes before buffering. Callers own and dispose the response.</summary>
internal static class BoundedResponseReader
{
    internal static async Task<byte[]?> ReadAsync(HttpContent content, long maxBytes, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        if (content.Headers.ContentLength is { } length && length > maxBytes)
        {
            return null;
        }

        using Stream source = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            while (true)
            {
                long remaining = maxBytes - output.Length;
                int count = remaining >= buffer.Length ? buffer.Length : (int)remaining + 1;
                int read = await source.ReadAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    return output.ToArray();
                }

                if (read > remaining)
                {
                    return null;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
