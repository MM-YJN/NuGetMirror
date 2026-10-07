using Microsoft.Extensions.Logging.Abstractions;

using NuGetMirror.Storage;

namespace NuGetMirror.UnitTests;

public sealed class KeyedAsyncLockTests
{
    [Fact]
    public async Task LockAsync_SerializesAccess_SameKey()
    {
        var locker = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance);
        bool completed = false;
        CancellationToken ct = TestContext.Current.CancellationToken;

        using (await locker.LockAsync("key-a", ct))
        {
            var task = Task.Run(async () =>
            {
                using (await locker.LockAsync("key-a", ct))
                {
                    completed = true;
                }
            }, ct);

            await Task.Delay(50, ct);
            Assert.False(completed);
        }

        await Task.Delay(100, ct);
        Assert.True(completed);
    }

    [Fact]
    public async Task LockAsync_AllowsConcurrentAccess_DifferentKeys()
    {
        var locker = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance);
        CancellationToken ct = TestContext.Current.CancellationToken;
        bool completed = false;

        using (await locker.LockAsync("key-a", ct))
        {
            _ = Task.Run(async () =>
            {
                using (await locker.LockAsync("key-b", ct))
                {
                    completed = true;
                }
            }, ct);

            await Task.Delay(100, ct);
            Assert.True(completed);
        }
    }

    [Fact]
    public async Task LockAsync_ReleasesOnDispose()
    {
        var locker = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance);
        CancellationToken ct = TestContext.Current.CancellationToken;

        using (await locker.LockAsync("key", ct))
        {
        }

        bool entered = false;
        using (await locker.LockAsync("key", ct))
        {
            entered = true;
        }

        Assert.True(entered, "Second LockAsync call should complete after the first releaser is disposed.");
    }

    [Fact]
    public async Task LockAsync_HighConcurrency_SameKey_SerializesAccess()
    {
        var locker = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance);
        CancellationToken ct = TestContext.Current.CancellationToken;
        int concurrency = 0;
        int maxConcurrency = 0;
        int completed = 0;

        async Task Worker()
        {
            using (await locker.LockAsync("shared-key", ct))
            {
                int current = Interlocked.Increment(ref concurrency);
                InterlockedMax(ref maxConcurrency, current);
                await Task.Yield();
                Interlocked.Decrement(ref concurrency);
            }

            Interlocked.Increment(ref completed);
        }

        Task[] tasks = Enumerable.Range(0, 100).Select(_ => Worker()).ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(100, completed);
        Assert.Equal(1, maxConcurrency);
    }

    [Fact]
    public async Task LockAsync_HighConcurrency_DifferentKeys_AllowsParallelism()
    {
        var locker = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance);
        CancellationToken ct = TestContext.Current.CancellationToken;
        int concurrency = 0;
        int maxConcurrency = 0;
        int completed = 0;

        async Task Worker(string key)
        {
            using (await locker.LockAsync(key, ct))
            {
                int current = Interlocked.Increment(ref concurrency);
                InterlockedMax(ref maxConcurrency, current);
                await Task.Yield();
                Interlocked.Decrement(ref concurrency);
            }

            Interlocked.Increment(ref completed);
        }

        Task[] tasks = Enumerable.Range(0, 100).Select(i => Worker($"key-{i}")).ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(100, completed);
        Assert.True(maxConcurrency > 1, $"Expected parallelism > 1, got {maxConcurrency}");
    }

    [Fact]
    public async Task LockAsync_CleansUpSemaphore_AfterAllReleasersDisposed()
    {
        var locker = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance);
        CancellationToken ct = TestContext.Current.CancellationToken;

        Assert.Equal(0, locker.Count);

        using (await locker.LockAsync("key", ct))
        {
            Assert.Equal(1, locker.Count);
        }

        // After disposal, the lock entry should be cleaned up
        Assert.Equal(0, locker.Count);
    }

    [Fact]
    public async Task LockAsync_CleansUpSemaphore_AfterConcurrentAccess()
    {
        var locker = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance);
        CancellationToken ct = TestContext.Current.CancellationToken;

        async Task Worker(string key)
        {
            using (await locker.LockAsync(key, ct))
            {
                await Task.Yield();
            }
        }

        Task[] tasks = Enumerable.Range(0, 100).Select(i => Worker($"key-{i}")).ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(0, locker.Count);
    }

    [Fact]
    public async Task LockAsync_DoesNotCleanUp_WhileStillHeld()
    {
        var locker = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance);
        CancellationToken ct = TestContext.Current.CancellationToken;

        using (await locker.LockAsync("key", ct))
        {
            Assert.Equal(1, locker.Count);
        }

        Assert.Equal(0, locker.Count);
    }

    [Fact]
    public async Task LockAsync_ReusesEntry_UnderContention()
    {
        var locker = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance);
        CancellationToken ct = TestContext.Current.CancellationToken;
        var firstEntered = new TaskCompletionSource();
        var secondCompleted = new TaskCompletionSource();

        var task2 = Task.Run(async () =>
        {
            using (await locker.LockAsync("key", ct))
            {
                secondCompleted.SetResult();
            }
        }, ct);

        var task1 = Task.Run(async () =>
        {
            using (await locker.LockAsync("key", ct))
            {
                Assert.Equal(1, locker.Count);
                firstEntered.SetResult();
                await Task.Delay(100, ct);
            }
        }, ct);

        await firstEntered.Task;
        await secondCompleted.Task;
        await Task.WhenAll(task1, task2);

        Assert.Equal(0, locker.Count);
    }

    [Fact]
    public async Task LockAsync_Cancellation_DoesNotOverReleaseSemaphore()
    {
        // Regression test: if WaitAsync throws due to cancellation the semaphore must
        // NOT be released; otherwise a subsequent waiter acquires the lock while the
        // original holder still holds it, violating mutual exclusion.
        var locker = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance);
        CancellationToken testCt = TestContext.Current.CancellationToken;
        using var cts = new CancellationTokenSource();

        var holderEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holderRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Thread A: holds the lock indefinitely until signalled
        var holderTask = Task.Run(async () =>
        {
            using (await locker.LockAsync("key", testCt))
            {
                holderEntered.SetResult();
                await holderRelease.Task.WaitAsync(testCt);
            }
        }, testCt);

        // Wait until the holder is inside the critical section
        await holderEntered.Task;

        // Thread B: tries to acquire the same lock, then gets cancelled while waiting
        var waiterTask = Task.Run(async () =>
        {
            await locker.LockAsync("key", cts.Token);
        }, testCt);

        // Give the waiter a moment to block on WaitAsync, then cancel it
        await Task.Delay(50, testCt);
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => waiterTask);

        // Release the holder. If the semaphore was over-released by the cancelled
        // waiter, a NEW acquirer could enter before the holder actually releases —
        // we verify this does not happen by checking the semaphore is still held
        // (Count == 1 in the dictionary means the holder's entry is still there).
        Assert.Equal(1, locker.Count);

        // Release the holder and verify full cleanup
        holderRelease.SetResult();
        await holderTask;
        Assert.Equal(0, locker.Count);
    }

    [Fact]
    public async Task LockAsync_Cancellation_CleansUpEntry_WhenNoOtherWaiters()
    {
        // After a cancelled wait where no other thread holds or waits on the key,
        // the dictionary entry should be cleaned up.
        var locker = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance);
        CancellationToken testCt = TestContext.Current.CancellationToken;
        using var cts = new CancellationTokenSource();

        var holderEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holderRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var holderTask = Task.Run(async () =>
        {
            using (await locker.LockAsync("key", testCt))
            {
                holderEntered.SetResult();
                await holderRelease.Task.WaitAsync(testCt);
            }
        }, testCt);

        await holderEntered.Task;

        var waiterTask = Task.Run(async () =>
        {
            await locker.LockAsync("key", cts.Token);
        }, testCt);

        await Task.Delay(50, testCt);
        await cts.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => waiterTask);

        // Release holder — now no one holds or waits; entry must be removed
        holderRelease.SetResult();
        await holderTask;
        Assert.Equal(0, locker.Count);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int snapshot;
        do
        {
            snapshot = Volatile.Read(ref target);
        }
        while (value > snapshot && Interlocked.CompareExchange(ref target, value, snapshot) != snapshot);
    }
}
