using System.Collections.Concurrent;
using System.Text;

namespace NuGetMirror.Discovery;

internal static class UrlRewriter
{
    // Minimal StringBuilder pool: avoids per-request LOH allocation for large bodies.
    // Pool size is soft-limited to 32 entries; StringBuilders with capacity beyond
    // MaxRetainedCapacity (256 K chars = 512 KB) are dropped rather than returned.
    private static readonly ConcurrentQueue<StringBuilder> s_stringBuilderPool = new();
    private static int s_sbPoolCount;
    private const int MaxPooled = 32;
    private const int MaxRetainedCapacity = 256 * 1024;

    public static string Rewrite(string body, IReadOnlyList<RewritePair> pairs, string mirrorBaseUrl, string[]? prebuiltTargets = null)
    {
        ValidateArguments(body, pairs, mirrorBaseUrl, prebuiltTargets);

        if (pairs.Count == 0)
        {
            return body;
        }

        using var scanner = new RewriteScanner(body, pairs, mirrorBaseUrl, prebuiltTargets);
        return scanner.Run();
    }

    private static void ValidateArguments(string body, IReadOnlyList<RewritePair> pairs, string mirrorBaseUrl, string[]? prebuiltTargets)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(mirrorBaseUrl);

        if (prebuiltTargets is not null && prebuiltTargets.Length != pairs.Count)
        {
            throw new ArgumentException(
                $"prebuiltTargets length ({prebuiltTargets.Length}) must equal pairs.Count ({pairs.Count}).",
                nameof(prebuiltTargets));
        }
    }

    internal static string[] BuildTargets(IReadOnlyList<RewritePair> pairs, string mirrorBaseUrl)
    {
        string[] targets = new string[pairs.Count];
        for (int i = 0; i < pairs.Count; i++)
        {
            targets[i] = mirrorBaseUrl + pairs[i].MirrorPrefix;
        }

        return targets;
    }

    private static StringBuilder RentStringBuilder(int capacityHint)
    {
        if (s_stringBuilderPool.TryDequeue(out StringBuilder? sb))
        {
            Interlocked.Decrement(ref s_sbPoolCount);
            sb.Clear();
            sb.EnsureCapacity(capacityHint);
            return sb;
        }

        return new StringBuilder(capacityHint);
    }

    private static void ReturnStringBuilder(StringBuilder sb)
    {
        // Drop oversized StringBuilders to prevent accumulating large char arrays in the pool.
        // MaxPooled is a soft cap: the count read and the subsequent Increment are not atomic,
        // so the pool can transiently hold up to MaxPooled + N_concurrent_threads entries.
        // This is intentional — a hard limit would require a lock that is not worth the cost
        // for an advisory pool.
        if (sb.Capacity > MaxRetainedCapacity || s_sbPoolCount >= MaxPooled)
        {
            return;
        }

        Interlocked.Increment(ref s_sbPoolCount);
        s_stringBuilderPool.Enqueue(sb);
    }

    private ref struct RewriteScanner(string body, IReadOnlyList<RewritePair> pairs, string mirrorBaseUrl, string[]? prebuiltTargets)
    {
        private int _pos;
        private int _spanStart;
        private StringBuilder? _result;

        public string Run()
        {
            string[]? targets = prebuiltTargets;

            while (_pos < body.Length)
            {
                int bestIdx = FindLongestMatchIndex(body.AsSpan(_pos));
                if (bestIdx >= 0)
                {
                    targets ??= BuildTargets(pairs, mirrorBaseUrl);
                    _result ??= RentStringBuilder(body.Length + (pairs.Count * 16));

                    EmitMatch(targets, bestIdx);
                }
                else
                {
                    _pos++;
                }
            }

            if (_result is null)
            {
                return body;
            }

            if (_spanStart < body.Length)
            {
                _result.Append(body, _spanStart, body.Length - _spanStart);
            }

            return _result.ToString();
        }

        private void EmitMatch(string[] targets, int bestIdx)
        {
            if (_pos > _spanStart)
            {
                _result!.Append(body, _spanStart, _pos - _spanStart);
            }

            _result!.Append(targets[bestIdx]);
            _pos += pairs[bestIdx].UpstreamPrefix.Length;
            _spanStart = _pos;
        }

        private readonly int FindLongestMatchIndex(ReadOnlySpan<char> tail)
        {
            int bestLen = 0;
            int bestIdx = -1;
            for (int i = 0; i < pairs.Count; i++)
            {
                string prefix = pairs[i].UpstreamPrefix;
                if (prefix.Length > bestLen && tail.StartsWith(prefix, StringComparison.Ordinal))
                {
                    bestLen = prefix.Length;
                    bestIdx = i;
                }
            }
            return bestIdx;
        }

        public void Dispose()
        {
            if (_result is not null)
            {
                ReturnStringBuilder(_result);
                _result = null;
            }
        }
    }
}
