using Microsoft.Extensions.Primitives;

namespace NuGetMirror.Proxy;

internal static class ConditionalRequest
{
    public static bool TryWriteNotModified(HttpContext context, string? etag)
    {
        if (etag is null)
        {
            return false;
        }

        StringValues ifNoneMatch = context.Request.Headers.IfNoneMatch;
        if (ifNoneMatch.Count == 0)
        {
            return false;
        }

        ReadOnlySpan<char> storedOpaque = ExtractOpaque(etag.AsSpan());

        bool match = false;

        foreach (string? headerValue in ifNoneMatch)
        {
            ReadOnlySpan<char> span = headerValue.AsSpan().Trim();

            if (span.Length == 0)
            {
                continue;
            }

            if (SegmentListMatches(span, storedOpaque))
            {
                match = true;
                break;
            }
        }

        if (!match)
        {
            return false;
        }

        context.Response.StatusCode = StatusCodes.Status304NotModified;
        context.Response.Headers.ETag = etag;
        return true;
    }

    private static bool SegmentListMatches(ReadOnlySpan<char> span, ReadOnlySpan<char> storedOpaque)
    {
        int start = 0;

        while (start < span.Length)
        {
            int comma = span[start..].IndexOf(',');
            ReadOnlySpan<char> segment = (comma >= 0 ? span[start..(start + comma)] : span[start..]).Trim();
            start += comma >= 0 ? comma + 1 : span.Length;

            if (segment.Length == 0)
            {
                continue;
            }

            if (segment.Length == 1 && segment[0] == '*')
            {
                return true;
            }

            if (TryParseEntityTag(segment, out ReadOnlySpan<char> tag) && tag.SequenceEqual(storedOpaque))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseEntityTag(ReadOnlySpan<char> segment, out ReadOnlySpan<char> tag)
    {
        tag = default;

        if (segment.StartsWith("W/", StringComparison.Ordinal))
        {
            segment = segment[2..];
        }

        if (segment.Length < 2 || segment[0] != '"')
        {
            return false;
        }

        int closingQuote = segment[1..].IndexOf('"');

        if (closingQuote < 0)
        {
            return false;
        }

        tag = segment.Slice(1, closingQuote);
        return true;
    }

    private static ReadOnlySpan<char> ExtractOpaque(ReadOnlySpan<char> etag)
    {
        if (etag.StartsWith("W/", StringComparison.Ordinal))
        {
            etag = etag[2..];
        }

        if (etag.Length >= 2 && etag[0] == '"' && etag[^1] == '"')
        {
            etag = etag[1..^1];
        }

        return etag;
    }
}
