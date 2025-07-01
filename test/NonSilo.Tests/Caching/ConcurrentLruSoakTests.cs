using System.Collections.Concurrent;
using System.Reflection;
using FluentAssertions;
using Orleans.Caching;
using Orleans.Caching.Internal;
using Xunit;
using Xunit.Abstractions;

namespace NonSilo.Tests.Caching;

/// <summary>
/// Stress tests for the ConcurrentLruCache to verify thread-safety and consistency under high concurrent load.
/// These "soak tests" run intensive multi-threaded operations to detect race conditions, deadlocks, and data corruption
/// that might not be caught by regular unit tests. The cache must maintain consistency of its multi-generation structure
/// even under extreme concurrent access patterns.
/// </summary>
[TestCategory("BVT")]
public sealed class ConcurrentLruCacheSoakTests
{
    private readonly ITestOutputHelper testOutputHelper;
    private const int HotCap = 3;
    private const int WarmCap = 3;
    private const int ColdCap = 3;

    private const int Capacity = HotCap + WarmCap + ColdCap;

    private ConcurrentLruCache<int, string> lru = new ConcurrentLruCache<int, string>(Capacity, EqualityComparer<int>.Default);

    public ConcurrentLruCacheSoakTests(ITestOutputHelper testOutputHelper)
    {
        this.testOutputHelper = testOutputHelper;
    }

    /// <summary>
    /// Tests concurrent GetOrAdd operations to ensure the cache maintains consistency under heavy read/write load.
    /// Verifies that the cache size remains within bounds and internal structures remain valid.
    /// </summary>
    [Fact]
    public async Task WhenSoakConcurrentGetCacheEndsInConsistentState()
    {
        for (var i = 0; i < 10; i++)
        {
            await Threaded.Run(4, () =>
            {
                for (var i = 0; i < 100000; i++)
                {
                    lru.GetOrAdd(i + 1, i => i.ToString());
                }
            });

            testOutputHelper.WriteLine($"{lru.HotCount} {lru.WarmCount} {lru.ColdCount}");
            testOutputHelper.WriteLine(string.Join(" ", lru.Keys));

            // allow +/- 1 variance for capacity
            lru.Count.Should().BeInRange(7, 10);
            RunIntegrityCheck();
        }
    }

    [Fact]
    public async Task WhenSoakConcurrentGetWithArgCacheEndsInConsistentState()
    {
        for (var i = 0; i < 10; i++)
        {
            await Threaded.Run(4, () =>
            {
                for (var i = 0; i < 100000; i++)
                {
                    // use the arg overload
                    lru.GetOrAdd(i + 1, (i, s) => i.ToString(), "Foo");
                }
            });

            testOutputHelper.WriteLine($"{lru.HotCount} {lru.WarmCount} {lru.ColdCount}");
            testOutputHelper.WriteLine(string.Join(" ", lru.Keys));

            // allow +/- 1 variance for capacity
            lru.Count.Should().BeInRange(7, 10);
            RunIntegrityCheck();
        }
    }

    [Fact]
    public async Task WhenSoakConcurrentGetAndRemoveCacheEndsInConsistentState()
    {
        for (var i = 0; i < 10; i++)
        {
            await Threaded.Run(4, () =>
            {
                for (var i = 0; i < 100000; i++)
                {
                    lru.TryRemove(i + 1);
                    lru.GetOrAdd(i + 1, i => i.ToString());
                }
            });

            testOutputHelper.WriteLine($"{lru.HotCount} {lru.WarmCount} {lru.ColdCount}");
            testOutputHelper.WriteLine(string.Join(" ", lru.Keys));

            RunIntegrityCheck();
        }
    }

    [Fact]
    public async Task WhenSoakConcurrentGetAndRemoveKvpCacheEndsInConsistentState()
    {
        for (var i = 0; i < 10; i++)
        {
            await Threaded.Run(4, () =>
            {
                for (var i = 0; i < 100000; i++)
                {
                    lru.TryRemove(new KeyValuePair<int, string>(i + 1, (i + 1).ToString()));
                    lru.GetOrAdd(i + 1, i => i.ToString());
                }
            });

            testOutputHelper.WriteLine($"{lru.HotCount} {lru.WarmCount} {lru.ColdCount}");
            testOutputHelper.WriteLine(string.Join(" ", lru.Keys));

            RunIntegrityCheck();
        }
    }

    [Fact]
    public async Task WhenSoakConcurrentGetAndUpdateCacheEndsInConsistentState()
    {
        for (var i = 0; i < 10; i++)
        {
            await Threaded.Run(4, () =>
            {
                for (var i = 0; i < 100000; i++)
                {
                    lru.TryUpdate(i + 1, i.ToString());
                    lru.GetOrAdd(i + 1, i => i.ToString());
                }
            });

            testOutputHelper.WriteLine($"{lru.HotCount} {lru.WarmCount} {lru.ColdCount}");
            testOutputHelper.WriteLine(string.Join(" ", lru.Keys));

            RunIntegrityCheck();
        }
    }

    [Fact]
    public async Task WhenSoakConcurrentGetAndAddCacheEndsInConsistentState()
    {
        for (var i = 0; i < 10; i++)
        {
            await Threaded.Run(4, () =>
            {
                for (var i = 0; i < 100000; i++)
                {
                    lru.AddOrUpdate(i + 1, i.ToString());
                    lru.GetOrAdd(i + 1, i => i.ToString());
                }
            });

            testOutputHelper.WriteLine($"{lru.HotCount} {lru.WarmCount} {lru.ColdCount}");
            testOutputHelper.WriteLine(string.Join(" ", lru.Keys));

            RunIntegrityCheck();
        }
    }

    [Fact]
    public async Task WhenSoakConcurrentGetAndUpdateValueTypeCacheEndsInConsistentState()
    {
        var lruVT = new ConcurrentLruCache<int, Guid>(Capacity, EqualityComparer<int>.Default);

        for (var i = 0; i < 10; i++)
        {
            await Threaded.Run(4, () =>
            {
                var b = new byte[8];
                for (var i = 0; i < 100000; i++)
                {
                    lruVT.TryUpdate(i + 1, new Guid(i, 0, 0, b));
                    lruVT.GetOrAdd(i + 1, x => new Guid(x, 0, 0, b));
                }
            });

            testOutputHelper.WriteLine($"{lruVT.HotCount} {lruVT.WarmCount} {lruVT.ColdCount}");
            testOutputHelper.WriteLine(string.Join(" ", lruVT.Keys));

            ConcurrentLruCacheIntegrityChecker.Validate(lruVT);
        }
    }

    [Fact]
    public async Task WhenAddingCacheSizeItemsNothingIsEvicted()
    {
        const int size = 1024;

        var cache = new ConcurrentLruCache<int, int>(size);

        await Threaded.Run(4, () =>
        {
            for (var i = 0; i < size; i++)
            {
                cache.GetOrAdd(i, k => k);
            }
        });

        cache.Metrics.Evicted.Should().Be(0);
    }

    [Fact]
    public async Task WhenConcurrentUpdateAndRemoveKvp()
    {
        var tcs = new TaskCompletionSource<int>();

        var removal = Task.Run(() =>
        {
            while (!tcs.Task.IsCompleted)
            {
                lru.TryRemove(new KeyValuePair<int, string>(5, "x"));
            }
        });

        for (var i = 0; i < 100_000; i++)
        {
            lru.AddOrUpdate(5, "a");
            lru.TryGet(5, out _).Should().BeTrue("key 'a' should not be deleted");
            lru.AddOrUpdate(5, "x");
        }

        tcs.SetResult(int.MaxValue);

        await removal;
    }

    [Theory]
    [Repeat(10)]
    public async Task WhenConcurrentGetAndClearCacheEndsInConsistentState(int iteration)
    {
        await Threaded.Run(4, r =>
        {
            for (var i = 0; i < 100000; i++)
            {
                // clear 6,250 times per 1_000_000 iters
                if (r == 0 && (i & 15) == 15)
                {
                    lru.Clear();
                }

                lru.GetOrAdd(i + 1, i => i.ToString());
            }
        });

        testOutputHelper.WriteLine($"{iteration} {lru.HotCount} {lru.WarmCount} {lru.ColdCount}");
        testOutputHelper.WriteLine(string.Join(" ", lru.Keys));

        RunIntegrityCheck();
    }

    [Theory]
    [Repeat(10)]
    public async Task WhenConcurrentGetAndClearDuringWarmupCacheEndsInConsistentState(int iteration)
    {
        await Threaded.Run(4, r =>
        {
            for (var i = 0; i < 100000; i++)
            {
                // clear 25,000 times per 1_000_000 iters
                // capacity is 9, so we will try to clear before warmup is done
                if (r == 0 && (i & 3) == 3)
                {
                    lru.Clear();
                }

                lru.GetOrAdd(i + 1, i => i.ToString());
            }
        });

        testOutputHelper.WriteLine($"{iteration} {lru.HotCount} {lru.WarmCount} {lru.ColdCount}");
        testOutputHelper.WriteLine(string.Join(" ", lru.Keys));

        RunIntegrityCheck();
    }

    // This test will run forever if there is a live lock.
    // Since the cache bookkeeping has some overhead, it is harder to provoke
    // spinning inside the reader thread compared to LruItemSoakTests.DetectTornStruct.
    [Theory]
    [Repeat(10)]
    public async Task WhenValueIsBigStructNoLiveLock(int _)
    {
        using var source = new CancellationTokenSource();
        var started = new TaskCompletionSource<bool>();
        var cache = new ConcurrentLruCache<int, Guid>(Capacity, EqualityComparer<int>.Default);

        var setTask = Task.Run(() => Setter(cache, source.Token, started));
        await started.Task;
        Checker(cache, source);

        await setTask;
    }

    private void Setter(ConcurrentLruCache<int, Guid> cache, CancellationToken cancelToken, TaskCompletionSource<bool> started)
    {
        started.SetResult(true);

        while (true)
        {
            cache.AddOrUpdate(1, Guid.NewGuid());
            cache.AddOrUpdate(1, Guid.NewGuid());

            if (cancelToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private void Checker(ConcurrentLruCache<int, Guid> cache, CancellationTokenSource source)
    {
        for (var count = 0; count < 100_000; ++count)
        {
            cache.TryGet(1, out _);
        }

        source.Cancel();
    }

    private void RunIntegrityCheck() => ConcurrentLruCacheIntegrityChecker.Validate(lru);

    private static class ConcurrentLruCacheIntegrityChecker
    {
        public static void Validate<K, V>(ConcurrentLruCache<K, V> cache)
        {
            ConcurrentLruCache<K, V>.ITestAccessor testAccessor = cache;
            // queue counters must be consistent with queues
            testAccessor.HotQueue.Count.Should().Be(cache.HotCount, "hot queue has a corrupted count");
            testAccessor.WarmQueue.Count.Should().Be(cache.WarmCount, "warm queue has a corrupted count");
            testAccessor.ColdQueue.Count.Should().Be(cache.ColdCount, "cold queue has a corrupted count");

            // cache contents must be consistent with queued items
            ValidateQueue(testAccessor.HotQueue, "hot");
            ValidateQueue(testAccessor.WarmQueue, "warm");
            ValidateQueue(testAccessor.ColdQueue, "cold");

            // cache must be within capacity
            cache.Count.Should().BeLessThanOrEqualTo(cache.Capacity + 1, "capacity out of valid range");

            void ValidateQueue(ConcurrentQueue<ConcurrentLruCache<K, V>.LruItem> queue, string queueName)
            {
                foreach (var item in queue)
                {
                    if (item.WasRemoved)
                    {
                        // It is possible for the queues to contain 2 (or more) instances of the same key/item. One that was removed,
                        // and one that was added after the other was removed.
                        // In this case, the dictionary may contain the value only if the queues contain an entry for that key marked as WasRemoved == false.
                        if (testAccessor.Dictionary.TryGetValue(item.Key, out var value))
                        {
                            testAccessor.HotQueue.Union(testAccessor.WarmQueue).Union(testAccessor.ColdQueue)
                                .Any(i => i.Key.Equals(item.Key) && !i.WasRemoved)
                                .Should().BeTrue($"{queueName} removed item {item.Key} was not removed");
                        }
                    }
                    else
                    {
                        testAccessor.Dictionary.TryGetValue(item.Key, out var value).Should().BeTrue($"{queueName} item {item.Key} was not present");
                    }
                }
            }
        }
    }

    private sealed class RepeatAttribute : Xunit.Sdk.DataAttribute
    {
        private readonly int _count;

        public RepeatAttribute(int count)
        {
            if (count < 1)
            {
                throw new ArgumentOutOfRangeException(
                    paramName: nameof(count),
                    message: "Repeat count must be greater than 0."
                    );
            }

            _count = count;
        }

        public override IEnumerable<object[]> GetData(System.Reflection.MethodInfo testMethod)
        {
            foreach (var iterationNumber in Enumerable.Range(start: 1, count: _count))
            {
                yield return new object[] { iterationNumber };
            }
        }
    }

    private class Threaded
    {
        public static Task Run(int threadCount, Action action)
        {
            return Run(threadCount, i => action());
        }

        public static async Task Run(int threadCount, Action<int> action)
        {
            var tasks = new Task[threadCount];
            ManualResetEvent mre = new ManualResetEvent(false);

            for (int i = 0; i < threadCount; i++)
            {
                int run = i; 
                tasks[i] = Task.Run(() =>
                {
                    mre.WaitOne();
                    action(run);
                });
            }

            mre.Set();

            await Task.WhenAll(tasks);
        }

        public static Task RunAsync(int threadCount, Func<Task> action)
        {
            return Run(threadCount, i => action());
        }

        public static async Task RunAsync(int threadCount, Func<int, Task> action)
        {
            var tasks = new Task[threadCount];
            ManualResetEvent mre = new ManualResetEvent(false);

            for (int i = 0; i < threadCount; i++)
            {
                int run = i;
                tasks[i] = Task.Run(async () =>
                {
                    mre.WaitOne();
                    await action(run);
                });
            }

            mre.Set();

            await Task.WhenAll(tasks);
        }
    }
}
