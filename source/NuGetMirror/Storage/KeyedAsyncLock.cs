using System.Collections.Concurrent;

using NuGetMirror.Diagnostics;

namespace NuGetMirror.Storage;

internal sealed partial class KeyedAsyncLock
{
    private readonly ILogger<KeyedAsyncLock> _logger;
    private readonly MirrorMetrics? _metrics;
    private readonly ConcurrentDictionary<string, RefCountedSemaphore> _locks = new(StringComparer.Ordinal);
    private readonly object _cleanupLock = new();

    internal int Count => _locks.Count;

    public KeyedAsyncLock(ILogger<KeyedAsyncLock> logger, MirrorMetrics? metrics = null)
    {
        _logger = logger;
        _metrics = metrics;
        metrics?.RegisterActiveLocksGauge(() => Count);
    }

    public async Task<IDisposable> LockAsync(string key, CancellationToken ct)
    {
        // GetOrAdd + Increment must be atomic w.r.t. Cleanup, which also
        // holds _cleanupLock. Without this lock, Cleanup can dispose the semaphore in
        // the window between GetOrAdd and Increment, causing a use-after-dispose.
        RefCountedSemaphore entry;

        lock (_cleanupLock)
        {
            entry = _locks.GetOrAdd(key, static _ => new RefCountedSemaphore(new SemaphoreSlim(1, 1)));
            Interlocked.Increment(ref entry._refCount);
        }

        if (entry._semaphore.CurrentCount == 0)
        {
            LogLockContended(key);
            _metrics?.RecordLockContended();
        }

        try
        {
            await entry._semaphore.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // WaitAsync threw without acquiring the semaphore (e.g. cancellation).
            // Only decrement the ref count; do NOT call Release(), which would over-release
            // and allow concurrent entries past the lock.
            if (Interlocked.Decrement(ref entry._refCount) == 0)
            {
                Cleanup(key, entry);
            }

            throw;
        }

        return new Releaser(key, entry, this);
    }

    internal void ReleaseAndTryCleanup(string key, RefCountedSemaphore entry)
    {
        entry._semaphore.Release();

        if (Interlocked.Decrement(ref entry._refCount) == 0)
        {
            Cleanup(key, entry);
        }
    }

    private void Cleanup(string key, RefCountedSemaphore entry)
    {
        lock (_cleanupLock)
        {
            if (Volatile.Read(ref entry._refCount) == 0)
            {
                _locks.TryRemove(key, out _);
                Interlocked.Exchange(ref entry._refCount, -1);
                entry._semaphore.Dispose();
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Lock contended for {Key}; waiting.")]
    private partial void LogLockContended(string key);

    internal sealed class RefCountedSemaphore(SemaphoreSlim semaphore)
    {
        public readonly SemaphoreSlim _semaphore = semaphore;
        public int _refCount;
    }

    private sealed class Releaser(string key, RefCountedSemaphore entry, KeyedAsyncLock owner) : IDisposable
    {
        public void Dispose() => owner.ReleaseAndTryCleanup(key, entry);
    }
}
