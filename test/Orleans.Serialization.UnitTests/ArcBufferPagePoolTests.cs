using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Serialization.Buffers;
using Xunit;

namespace Orleans.Serialization.UnitTests;

[Trait("Category", "BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Serialization")]
public sealed class ArcBufferPagePoolTests
{
    private const int PageSize = ArcBufferWriter.MinimumPageSize;

    [Fact]
    public void DefaultBudget_RetainsExactly128MiB()
    {
        var pool = new ArcBufferPagePool();
        const int budget = 128 * 1024 * 1024;
        Assert.Equal(budget, pool.MaxRetainedBytes);
        Assert.Equal(pool.MaxRetainedBytes, ArcBufferWriter.MaxRetainedPoolBytes);
        Assert.Equal(0, pool.RetainedBytes);
        Assert.Equal(0, pool.RetainedPages);

        var pageCount = budget / PageSize;
        var pages = Enumerable.Range(0, pageCount + 1).Select(_ => pool.Rent()).ToArray();
        foreach (var page in pages) pool.Return(page);

        Assert.Equal(budget, pool.RetainedBytes);
        Assert.Equal(pageCount, pool.RetainedPages);
        Assert.Empty(pages[^1].Array);
        Assert.All(pages[..^1], page => Assert.Equal(PageSize, page.Array.Length));
        pool.MaxRetainedBytes = 0;
        Assert.All(pages, page => Assert.Empty(page.Array));
    }

    [Fact]
    public async Task PublicProperty_ForwardsConfigurationAndPreservesPinnedSlicesInIsolatedProcess()
    {
        const string childProcessVariable = "ORLEANS_ARC_POOL_PUBLIC_CONFIGURATION_TEST";
        if (Environment.GetEnvironmentVariable(childProcessVariable) == "1")
        {
            AssertPublicConfiguration();
            return;
        }

        // Run the same test in a fresh test process: restoring the limit alone would not restore trimmed Shared pages.
        var originalBudget = ArcBufferWriter.MaxRetainedPoolBytes;
        var processPath = Environment.ProcessPath!;
        var startInfo = new ProcessStartInfo(processPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(typeof(ArcBufferPagePoolTests).Assembly.Location);
        }

        startInfo.ArgumentList.Add("--filter-class");
        startInfo.ArgumentList.Add(typeof(ArcBufferPagePoolTests).FullName!);
        startInfo.ArgumentList.Add("--filter-method");
        startInfo.ArgumentList.Add("*" + nameof(PublicProperty_ForwardsConfigurationAndPreservesPinnedSlicesInIsolatedProcess));
        startInfo.ArgumentList.Add("--minimum-expected-tests");
        startInfo.ArgumentList.Add("1");
        startInfo.Environment[childProcessVariable] = "1";
        using var process = Process.Start(startInfo)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, $"Public configuration child test failed (exit {process.ExitCode}).\n{await output}\n{await errors}");
            Assert.Equal(originalBudget, ArcBufferWriter.MaxRetainedPoolBytes);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    private static void AssertPublicConfiguration()
    {
        var shared = ArcBufferPagePool.Shared;
        Assert.Equal(128 * 1024 * 1024, ArcBufferWriter.MaxRetainedPoolBytes);
        Assert.Equal(ArcBufferWriter.MaxRetainedPoolBytes, shared.MaxRetainedBytes);

        // Exercise valid writes through the public API, observing the underlying pool rather than just the getter.
        ArcBufferWriter.MaxRetainedPoolBytes = 0;
        Assert.Equal(0, ArcBufferWriter.MaxRetainedPoolBytes);
        Assert.Equal(0, shared.MaxRetainedBytes);
        Assert.Equal(0, shared.RetainedBytes);
        Assert.Equal(0, shared.RetainedPages);
        ArcBufferWriter.MaxRetainedPoolBytes = 3 * PageSize;
        Assert.Equal(3 * PageSize, ArcBufferWriter.MaxRetainedPoolBytes);
        Assert.Equal(3 * PageSize, shared.MaxRetainedBytes);
        var free = Enumerable.Range(0, 3).Select(_ => shared.Rent()).ToArray();
        foreach (var page in free) shared.Return(page);
        Assert.Equal(3 * PageSize, shared.RetainedBytes);
        Assert.Equal(3, shared.RetainedPages);
        foreach (var value in new[] { -1, int.MinValue })
        {
            Assert.Throws<ArgumentOutOfRangeException>("value", () => ArcBufferWriter.MaxRetainedPoolBytes = value);
            Assert.Equal(3 * PageSize, ArcBufferWriter.MaxRetainedPoolBytes);
            Assert.Equal(3 * PageSize, shared.MaxRetainedBytes);
            Assert.Equal(3 * PageSize, shared.RetainedBytes);
            Assert.Equal(3, shared.RetainedPages);
            Assert.All(free, page => Assert.Equal(PageSize, page.Array.Length));
        }

        ArcBufferWriter.MaxRetainedPoolBytes = 0;
        Assert.All(free, page => Assert.Empty(page.Array));
        Assert.Equal(0, shared.RetainedBytes);
        Assert.Equal(0, shared.RetainedPages);
        var expected = new byte[] { 1, 2, 3, 4 };
        using var writer = new ArcBufferWriter();
        writer.Write(expected);
        var slice = writer.PeekSlice(expected.Length);
        var active = Assert.IsType<ArcBufferPage>(slice.First);
        var array = active.Array;
        var version = active.Version;
        try
        {
            writer.Dispose();
            Assert.Equal(1, active.ReferenceCount);
            Assert.Equal(expected, slice.ToArray());
            Assert.Equal(0, shared.RetainedBytes);

            ArcBufferWriter.MaxRetainedPoolBytes = PageSize;
            Assert.Equal(PageSize, ArcBufferWriter.MaxRetainedPoolBytes);
            Assert.Equal(PageSize, shared.MaxRetainedBytes);
            var other = shared.Rent();
            Assert.NotSame(active, other);
            shared.Return(other);
            Assert.Equal(PageSize, shared.RetainedBytes);
            Assert.Equal(1, shared.RetainedPages);

            ArcBufferWriter.MaxRetainedPoolBytes = 0;
            Assert.Empty(other.Array);
            Assert.Same(array, active.Array);
            Assert.Equal(version, active.Version);
            Assert.Equal(expected, slice.ToArray());
        }
        finally
        {
            slice.Dispose();
        }

        Assert.Empty(active.Array);
        Assert.Equal(version + 1, active.Version);
        Assert.Equal(0, shared.RetainedBytes);
        Assert.Equal(0, shared.RetainedPages);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void NegativeBudget_IsRejectedWithoutChangingConfigurationOrFreePages(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>("maximumRetainedBytes", () => new ArcBufferPagePool(value));
        var pool = new ArcBufferPagePool(PageSize);
        var page = pool.Rent();
        var array = page.Array;
        pool.Return(page);
        var sharedBudget = ArcBufferWriter.MaxRetainedPoolBytes;

        Assert.Throws<ArgumentOutOfRangeException>("value", () => pool.MaxRetainedBytes = value);
        Assert.Throws<ArgumentOutOfRangeException>("value", () => ArcBufferWriter.MaxRetainedPoolBytes = value);

        Assert.Equal(sharedBudget, ArcBufferWriter.MaxRetainedPoolBytes);
        Assert.Equal(PageSize, pool.MaxRetainedBytes);
        Assert.Equal(PageSize, pool.RetainedBytes);
        Assert.Equal(1, pool.RetainedPages);
        Assert.Same(array, page.Array);
        Assert.Same(page, pool.Rent());
        pool.MaxRetainedBytes = 0;
        pool.Return(page);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(PageSize - 1)]
    public void BudgetSmallerThanOnePage_DoesNotRetainAnyPages(int budget)
    {
        var pool = new ArcBufferPagePool(budget);
        var minimum = pool.Rent();
        var large = pool.Rent(2 * PageSize);

        pool.Return(minimum);
        pool.Return(large);

        Assert.Equal(budget, pool.MaxRetainedBytes);
        Assert.Equal(0, pool.RetainedBytes);
        Assert.Equal(0, pool.RetainedPages);
        Assert.Empty(minimum.Array);
        Assert.Empty(large.Array);
    }

    [Fact]
    public void ShrinkingPopulatedPool_ReleasesExcessMinimumPagesImmediately()
    {
        var pool = new ArcBufferPagePool();
        var minimum = Enumerable.Range(0, 4).Select(_ => pool.Rent()).ToArray();
        var large = Enumerable.Range(0, 2).Select(_ => pool.Rent(1024 * 1024)).ToArray();
        foreach (var page in minimum.Concat(large)) pool.Return(page);
        Assert.Equal(4 * PageSize, pool.RetainedBytes);
        Assert.Equal(4, pool.RetainedPages);
        Assert.All(large, page => Assert.Empty(page.Array));

        pool.MaxRetainedBytes = 3 * PageSize;

        Assert.Equal(3 * PageSize, pool.RetainedBytes);
        Assert.Equal(3, pool.RetainedPages);
        Assert.All(large, page => Assert.Empty(page.Array));
        Assert.Empty(minimum[0].Array);
        foreach (var expected in minimum[1..]) Assert.Same(expected, pool.Rent());
        Assert.Equal(0, pool.RetainedBytes);
        Assert.Equal(0, pool.RetainedPages);
        pool.MaxRetainedBytes = 0;
        foreach (var page in minimum[1..]) pool.Return(page);
    }

    [Fact]
    public void SettingZero_ReleasesAllFreePagesAndDisablesFurtherRetention()
    {
        var pool = new ArcBufferPagePool();
        var minimum = pool.Rent();
        var large = pool.Rent(2 * PageSize);
        pool.Return(minimum);
        pool.Return(large);

        pool.MaxRetainedBytes = 0;

        Assert.Equal(0, pool.RetainedBytes);
        Assert.Equal(0, pool.RetainedPages);
        Assert.Empty(minimum.Array);
        Assert.Empty(large.Array);
        var subsequent = pool.Rent();
        Assert.Equal(PageSize, subsequent.Array.Length);
        pool.Return(subsequent);
        Assert.Empty(subsequent.Array);
        Assert.Equal(0, pool.RetainedBytes);
        Assert.Equal(0, pool.RetainedPages);
    }

    [Fact]
    public void RaisingBudget_KeepsExistingPagesAndAllowsMoreRetention()
    {
        var pool = new ArcBufferPagePool(PageSize);
        var minimum = pool.Rent();
        var extra = pool.Rent();
        var minimumArray = minimum.Array;
        pool.Return(minimum);

        pool.MaxRetainedBytes = 2 * PageSize;
        pool.Return(extra);

        Assert.Equal(2 * PageSize, pool.RetainedBytes);
        Assert.Equal(2, pool.RetainedPages);
        Assert.Same(minimumArray, minimum.Array);
        Assert.Same(minimum, pool.Rent());
        Assert.Same(extra, pool.Rent());
        Assert.Equal(0, pool.RetainedBytes);
        Assert.Equal(0, pool.RetainedPages);
        pool.MaxRetainedBytes = 0;
        foreach (var page in new[] { minimum, extra }) pool.Return(page);
    }

    [Theory]
    [InlineData(2 * PageSize)]
    [InlineData(1024 * 1024)]
    [InlineData(2 * 1024 * 1024)]
    public void LargePages_ReturnToArrayPoolWithoutUsingThePinnedPageBudget(int size)
    {
        var pool = new ArcBufferPagePool(0);
        pool.MaxRetainedBytes = int.MaxValue;
        var minimum = pool.Rent();
        var large = pool.Rent(size);

        pool.Return(minimum);
        pool.Return(large);

        Assert.Equal(int.MaxValue, pool.MaxRetainedBytes);
        Assert.Equal(PageSize, pool.RetainedBytes);
        Assert.Equal(1, pool.RetainedPages);
        Assert.Empty(large.Array);
        Assert.Same(minimum, pool.Rent());
        Assert.Equal(0, pool.RetainedBytes);
        Assert.Equal(0, pool.RetainedPages);
        pool.MaxRetainedBytes = 0;
        pool.Return(minimum);
        Assert.Empty(minimum.Array);
    }

    [Theory]
    [InlineData(PageSize)]
    [InlineData(2 * PageSize)]
    public void BudgetChanges_DoNotReleaseRentedPagesOrActivePinnedSlices(int size)
    {
        var pool = new ArcBufferPagePool();
        var active = pool.Rent(size);
        var array = active.Array;
        var version = active.Version;
        active.Pin(version);
        var expected = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        expected.CopyTo(array, 0);
        active.SetLength(expected.Length, version);
        var owner = new ArcBuffer(active, version, 0, expected.Length);
        using var slice = owner.Slice(8, 16);
        try
        {
            var free = pool.Rent();
            pool.Return(free);
            Assert.Equal(PageSize, pool.RetainedBytes); // The active page and its pins are outside the budget.

            pool.MaxRetainedBytes = 0;

            Assert.Empty(free.Array);
            Assert.Equal(0, pool.RetainedBytes);
            Assert.Equal(0, pool.RetainedPages);
            Assert.Same(array, active.Array);
            Assert.Equal(version, active.Version);
            Assert.Equal(2, active.ReferenceCount);
            Assert.Equal(expected, owner.ToArray());
            Assert.Equal(expected[8..24], slice.ToArray());
        }
        finally
        {
            owner.Dispose();
        }

        // Enable retention and reuse free pages after the original owner releases its pin, leaving only the reader.
        Assert.Equal(1, active.ReferenceCount);
        pool.MaxRetainedBytes = size;
        for (var i = 0; i < 8; i++)
        {
            var reused = pool.Rent(size);
            Assert.NotSame(active, reused);
            Assert.NotSame(array, reused.Array);
            reused.Array.AsSpan().Fill(255);
            pool.Return(reused);
            Assert.Equal(expected[8..24], slice.ToArray());
        }

        Assert.Same(array, active.Array);
        Assert.Equal(version, active.Version);
        Assert.Equal(size == PageSize ? PageSize : 0, pool.RetainedBytes);
        Assert.Equal(size == PageSize ? 1 : 0, pool.RetainedPages);
        pool.MaxRetainedBytes = 0;
        // ArcBufferPage.Unpin returns the reader-owned page to Shared, whose configuration is unchanged by this test.
    }

    [Fact]
    public async Task LatePublishedReturn_AfterShrink_TrimsToCurrentPageLimit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var pool = new ArcBufferPagePool(3 * PageSize);
        var alreadyFree = pool.Rent();
        var pending = pool.Rent();
        var rejected = pool.Rent();
        pool.Return(alreadyFree);
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returning = Task.Run(async () =>
        {
            // A return can observe capacity before the setter runs and publish after it finishes.
            Assert.True(pool.RetainedPages < pool.MaxRetainedBytes / PageSize);
            admitted.SetResult();
            await publish.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            pool.Publish(pending);
        }, cancellationToken);

        try
        {
            await admitted.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            Assert.Equal(PageSize, pool.RetainedBytes);
            Assert.Equal(1, pool.RetainedPages);

            pool.MaxRetainedBytes = 0;

            Assert.Empty(alreadyFree.Array); // Already-queued free pages are released before the setter returns.
            Assert.Equal(PageSize, pending.Array.Length);
            Assert.Equal(0, pool.RetainedBytes);
            Assert.Equal(0, pool.RetainedPages);
            pool.Return(rejected);
            Assert.Empty(rejected.Array);
            Assert.Equal(0, pool.RetainedBytes);
            Assert.Equal(0, pool.RetainedPages);
        }
        finally
        {
            publish.TrySetResult();
            await returning.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }

        Assert.Empty(pending.Array); // A late publication rechecks the limit without separate reservations.
        Assert.Equal(0, pool.RetainedBytes);
        Assert.Equal(0, pool.RetainedPages);
        pool.MaxRetainedBytes = PageSize;
        var subsequent = pool.Rent();
        pool.Return(subsequent);
        Assert.Equal(PageSize, pool.RetainedBytes);
        Assert.Equal(1, pool.RetainedPages);
        pool.MaxRetainedBytes = 0;
        Assert.Empty(subsequent.Array);
    }

    [Fact]
    public async Task StaleTrim_AfterIncrease_PreservesAllFreePages()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var pool = new ArcBufferPagePool(2 * PageSize);
        var pending = pool.Rent();
        var other = pool.Rent();
        var pendingArray = pending.Array;
        var otherArray = other.Array;
        pool.MaxRetainedBytes = 0;
        var overflowObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var trim = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var trimming = Task.Run(async () =>
        {
            overflowObserved.SetResult();
            await trim.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            pool.Trim(); // A trim request rechecks the limit instead of acting on an earlier observation.
        }, cancellationToken);

        try
        {
            await overflowObserved.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            pool.MaxRetainedBytes = 2 * PageSize;
            pool.Publish(pending);
            pool.Return(other);
            Assert.Equal(2 * PageSize, pool.RetainedBytes);
            Assert.Equal(2, pool.RetainedPages);
        }
        finally
        {
            trim.TrySetResult();
            await trimming.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }

        Assert.Equal(2 * PageSize, pool.MaxRetainedBytes);
        Assert.Equal(2 * PageSize, pool.RetainedBytes);
        Assert.Equal(2, pool.RetainedPages);
        Assert.Same(pendingArray, pending.Array);
        Assert.Same(otherArray, other.Array);
        Assert.Same(pending, pool.Rent());
        Assert.Same(other, pool.Rent());
        Assert.Equal(0, pool.RetainedBytes);
        Assert.Equal(0, pool.RetainedPages);
        pool.MaxRetainedBytes = 0;
        pool.Return(pending);
        pool.Return(other);
    }

    [Fact]
    public void PageBudget_RoundsDownWithoutChangingTheConfiguredByteLimit()
    {
        var pool = new ArcBufferPagePool(2 * PageSize - 1);
        var first = pool.Rent();
        var second = pool.Rent();
        pool.Return(first);
        pool.Return(second);
        Assert.Equal(2 * PageSize - 1, pool.MaxRetainedBytes);
        Assert.Equal(1, pool.RetainedPages);
        Assert.Equal(PageSize, pool.RetainedBytes);
        Assert.Empty(second.Array);

        pool.MaxRetainedBytes = 2 * PageSize;
        var third = new ArcBufferPage(PageSize);
        pool.Return(third);
        Assert.Equal(2, pool.RetainedPages);
        pool.MaxRetainedBytes = 2 * PageSize - 1;
        Assert.Equal(1, pool.RetainedPages);
        Assert.Empty(first.Array);
        Assert.Equal(PageSize, third.Array.Length);
        pool.MaxRetainedBytes = 0;
        Assert.Empty(third.Array);
    }

    [Theory]
    [InlineData(1, 0, 199)]
    [InlineData(1, 1, 198)]
    [InlineData(0, 40, 238)]
    [InlineData(0, 1, 201)]
    [InlineData(0, 0, 190)]
    public void Eviction_AdaptsToGrowingSteadyDecliningAndIdleDemand(int rents, int returns, int expectedPages)
    {
        var pool = new ArcBufferPagePool();
        var pages = Enumerable.Range(0, 240).Select(_ => pool.Rent()).ToArray();
        foreach (var page in pages[..200])
        {
            pool.Return(page);
        }

        // Initial misses represent growing demand and must leave the newly returned working set intact.
        pool.Evict();
        Assert.Equal(200, pool.RetainedPages);
        var held = pages[200..].ToList();
        for (var i = 0; i < rents; i++)
        {
            held.Add(pool.Rent());
        }

        for (var i = 0; i < returns; i++)
        {
            pool.Return(held[^1]);
            held.RemoveAt(held.Count - 1);
        }

        pool.Evict();

        Assert.Equal(expectedPages, pool.RetainedPages);
        Assert.Equal((long)expectedPages * PageSize, pool.RetainedBytes);
        Assert.Equal(200 - rents + returns - expectedPages, pages.Count(static page => page.Array.Length == 0));
        Assert.Equal(128 * 1024 * 1024, pool.MaxRetainedBytes);
        pool.MaxRetainedBytes = 0;
        foreach (var page in held)
        {
            pool.Return(page);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(200)]
    public void Eviction_RepeatedIdleTicksDrainTheCacheBelowItsCeiling(int count)
    {
        var pool = new ArcBufferPagePool();
        var pages = Enumerable.Range(0, count).Select(_ => pool.Rent()).ToArray();
        foreach (var page in pages)
        {
            pool.Return(page);
        }

        pool.Evict();
        Assert.Equal(count, pool.RetainedPages);
        var expected = count;
        while (expected > 0)
        {
            expected = Math.Max(0, expected - Math.Max(10, expected / 20));
            pool.Evict();
            Assert.Equal(expected, pool.RetainedPages);
            Assert.Equal((long)expected * PageSize, pool.RetainedBytes);
        }

        pool.Evict();
        Assert.Equal(0, pool.RetainedBytes);
        Assert.All(pages, static page => Assert.Empty(page.Array));
        Assert.Equal(128 * 1024 * 1024, pool.MaxRetainedBytes);
    }

    [Fact]
    public void Eviction_LargePageActivityDoesNotKeepPinnedPagesAlive()
    {
        var pool = new ArcBufferPagePool();
        var pages = Enumerable.Range(0, 200).Select(_ => pool.Rent()).ToArray();
        foreach (var page in pages)
        {
            pool.Return(page);
        }

        pool.Evict();
        Assert.Equal(200, pool.RetainedPages);
        for (var i = 0; i < 50; i++)
        {
            var large = pool.Rent(2 * PageSize);
            pool.Return(large);
            Assert.Empty(large.Array);
        }

        pool.Evict();

        Assert.Equal(190, pool.RetainedPages);
        Assert.Equal(190L * PageSize, pool.RetainedBytes);
        Assert.All(pages[..10], static page => Assert.Empty(page.Array));
        pool.MaxRetainedBytes = 0;
    }

    [Fact]
    public void Eviction_PreservesActivePinnedSlices()
    {
        var pool = new ArcBufferPagePool();
        var active = pool.Rent();
        var token = active.Version;
        active.Pin(token);
        active.Array[0] = 42;
        active.SetLength(1, token);
        var owner = new ArcBuffer(active, token, 0, 1);
        using var reader = owner.Slice(0);
        owner.Dispose();
        var pages = Enumerable.Range(0, 20).Select(_ => pool.Rent()).ToArray();
        foreach (var page in pages)
        {
            pool.Return(page);
        }

        pool.Evict();
        Assert.Equal(20, pool.RetainedPages);
        pool.Evict();
        Assert.Equal(10, pool.RetainedPages);
        pool.Evict();

        Assert.Equal(0, pool.RetainedBytes);
        Assert.All(pages, static page => Assert.Empty(page.Array));
        Assert.Equal(1, active.ReferenceCount);
        Assert.Equal(token, active.Version);
        Assert.Equal(new byte[] { 42 }, reader.ToArray());
    }

    [Fact]
    public void Eviction_WithAnUnpublishedReturn_DoesNotCountOrReleaseIt()
    {
        var pool = new ArcBufferPagePool();
        var published = pool.Rent();
        var pending = pool.Rent();
        pool.Return(published);
        pool.Evict();
        pool.Evict();
        Assert.Empty(published.Array);
        Assert.Equal(PageSize, pending.Array.Length);
        Assert.Equal(0, pool.RetainedBytes);

        pool.Publish(pending);
        Assert.Equal(PageSize, pool.RetainedBytes);
        pool.Evict();
        Assert.Empty(pending.Array);
        Assert.Equal(0, pool.RetainedBytes);
    }

    [Fact]
    public async Task ConcurrentRentReturnEvictionAndBudgetChanges_PreserveOwnershipAndExactAccounting()
    {
        const int workerCount = 4;
        const int rounds = 100;
        var cancellationToken = TestContext.Current.CancellationToken;
        var pool = new ArcBufferPagePool();
        var activePages = new ConcurrentDictionary<ArcBufferPage, byte>();
        var activeArrays = new ConcurrentDictionary<byte[], byte>();
        using var barrier = new Barrier(workerCount + 2);
        var workers = Enumerable.Range(0, workerCount + 2).Select(worker => Task.Factory.StartNew(() =>
        {
            for (var round = 0; round < rounds; round++)
            {
                Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(30), cancellationToken), $"Worker {worker}, round {round}: start barrier timed out.");
                if (worker < workerCount)
                {
                    for (var iteration = 0; iteration < 32; iteration++)
                    {
                        var size = worker % 2 == 0 ? PageSize : 2 * PageSize;
                        var page = pool.Rent(size);
                        var array = page.Array;
                        Assert.Equal(size, array.Length);
                        Assert.True(activePages.TryAdd(page, 0), "A live page was rented twice.");
                        Assert.True(activeArrays.TryAdd(array, 0), "A live backing array was rented twice.");
                        var marker = (byte)(worker * 32 + iteration);
                        array.AsSpan().Fill(marker);
                        Assert.Same(array, page.Array);
                        Assert.Equal(marker, page.Array[0]);
                        Assert.Equal(marker, page.Array[^1]);
                        Assert.True(activePages.TryRemove(page, out _));
                        Assert.True(activeArrays.TryRemove(array, out _));
                        pool.Return(page);
                    }
                }
                else if (worker == workerCount)
                {
                    for (var iteration = 0; iteration < 32; iteration++)
                    {
                        pool.MaxRetainedBytes = ((round + iteration + worker) % 3) switch
                        {
                            0 => 0,
                            1 => 2 * PageSize,
                            _ => 4 * 1024 * 1024,
                        };
                    }
                }
                else
                {
                    for (var iteration = 0; iteration < 32; iteration++)
                    {
                        pool.Evict();
                    }
                }

                Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(30), cancellationToken), $"Worker {worker}, round {round}: end barrier timed out.");
                // No rents, returns, or setters can run until all workers pass the next start barrier.
                Assert.True(pool.RetainedBytes <= pool.MaxRetainedBytes, "The settled pool exceeded its configured budget.");
                Assert.Equal((long)pool.RetainedPages * PageSize, pool.RetainedBytes);
            }
        }, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
        Assert.Empty(activePages);
        Assert.Empty(activeArrays);
        pool.MaxRetainedBytes = 0;
        Assert.Equal(0, pool.RetainedBytes);
        Assert.Equal(0, pool.RetainedPages);

        // Verify exact accounting and reuse after contention, not merely an upper bound.
        pool.MaxRetainedBytes = 8 * PageSize;
        var pages = Enumerable.Range(0, 9).Select(_ => pool.Rent()).ToArray();
        foreach (var page in pages) pool.Return(page);
        Assert.Equal(8 * PageSize, pool.RetainedBytes);
        Assert.Equal(8, pool.RetainedPages);
        Assert.Empty(pages[^1].Array);
        foreach (var expected in pages[..^1]) Assert.Same(expected, pool.Rent());
        Assert.Equal(0, pool.RetainedBytes);
        Assert.Equal(0, pool.RetainedPages);
        pool.MaxRetainedBytes = 0;
        foreach (var page in pages[..^1]) pool.Return(page);
    }
}
