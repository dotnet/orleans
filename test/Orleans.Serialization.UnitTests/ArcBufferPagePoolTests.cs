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
    public void DefaultBudget_RetainsExactlyFourMiB()
    {
        var pool = new ArcBufferPagePool();
        Assert.Equal(4 * 1024 * 1024, pool.MaxRetainedBytes);
        Assert.Equal(pool.MaxRetainedBytes, ArcBufferWriter.MaxRetainedPoolBytes);
        Assert.Equal(0, pool.RetainedBytes);
        Assert.Equal(0, pool.RetainedPages);

        var pages = Enumerable.Range(0, 257).Select(_ => pool.Rent()).ToArray();
        foreach (var page in pages) pool.Return(page);

        Assert.Equal(4 * 1024 * 1024, pool.RetainedBytes);
        Assert.Equal(256, pool.RetainedPages);
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
        Assert.Equal(4 * 1024 * 1024, ArcBufferWriter.MaxRetainedPoolBytes);
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
        var active = slice.First;
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
    public void ShrinkingPopulatedPool_ReleasesExcessFromBothQueuesImmediately()
    {
        var pool = new ArcBufferPagePool();
        var minimum = Enumerable.Range(0, 4).Select(_ => pool.Rent()).ToArray();
        var large = Enumerable.Range(0, 2).Select(_ => pool.Rent(1024 * 1024)).ToArray();
        foreach (var page in minimum.Concat(large)) pool.Return(page);
        Assert.Equal(4 * PageSize + 2 * 1024 * 1024, pool.RetainedBytes);
        Assert.Equal(6, pool.RetainedPages);

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
        var pool = new ArcBufferPagePool(3 * PageSize);
        var minimum = pool.Rent();
        var large = pool.Rent(2 * PageSize);
        var extra = pool.Rent();
        var minimumArray = minimum.Array;
        var largeArray = large.Array;
        pool.Return(minimum);
        pool.Return(large);

        pool.MaxRetainedBytes = 4 * PageSize;
        pool.Return(extra);

        Assert.Equal(4 * PageSize, pool.RetainedBytes);
        Assert.Equal(3, pool.RetainedPages);
        Assert.Same(minimumArray, minimum.Array);
        Assert.Same(largeArray, large.Array);
        Assert.Same(minimum, pool.Rent());
        Assert.Same(extra, pool.Rent());
        Assert.Same(large, pool.Rent(2 * PageSize));
        Assert.Equal(0, pool.RetainedBytes);
        Assert.Equal(0, pool.RetainedPages);
        pool.MaxRetainedBytes = 0;
        foreach (var page in new[] { minimum, large, extra }) pool.Return(page);
    }

    [Fact]
    public void RaisingFromZero_EnablesRetentionWithoutChangingIndividualPageLimit()
    {
        var pool = new ArcBufferPagePool(0);
        pool.MaxRetainedBytes = int.MaxValue;
        var cacheable = pool.Rent(1024 * 1024);
        var oversized = pool.Rent(2 * 1024 * 1024);

        pool.Return(cacheable);
        pool.Return(oversized);

        Assert.Equal(int.MaxValue, pool.MaxRetainedBytes);
        Assert.Equal(1024 * 1024, pool.RetainedBytes);
        Assert.Equal(1, pool.RetainedPages);
        Assert.Equal(1024 * 1024, cacheable.Array.Length);
        Assert.Empty(oversized.Array);
        pool.MaxRetainedBytes = 0;
        Assert.Empty(cacheable.Array);
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
            var free = pool.Rent(size);
            pool.Return(free);
            Assert.Equal(size, pool.RetainedBytes); // The active page and its pins are outside the budget.

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
        Assert.Equal(size, pool.RetainedBytes);
        Assert.Equal(1, pool.RetainedPages);
        pool.MaxRetainedBytes = 0;
        // ArcBufferPage.Unpin returns the reader-owned page to Shared, whose configuration is unchanged by this test.
    }

    [Fact]
    public async Task LatePublishedReturn_AfterShrink_TrimsExcessWithoutLosingReservations()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var pool = new ArcBufferPagePool(3 * PageSize);
        var alreadyFree = pool.Rent();
        var pending = pool.Rent();
        var rejected = pool.Rent();
        pool.Return(alreadyFree);
        var reserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returning = Task.Run(async () =>
        {
            // Execute the real return phases, holding publication until the setter finishes trimming.
            Assert.True(pool.TryReserve(pending.Array.Length));
            reserved.SetResult();
            await publish.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            pool.Publish(pending);
        }, cancellationToken);

        try
        {
            await reserved.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            Assert.Equal(2 * PageSize, pool.RetainedBytes);
            Assert.Equal(1, pool.RetainedPages);

            pool.MaxRetainedBytes = 0;

            Assert.Empty(alreadyFree.Array); // Already-queued free pages are released before the setter returns.
            Assert.Equal(PageSize, pending.Array.Length);
            Assert.Equal(PageSize, pool.RetainedBytes); // Only the unpublished reservation remains, temporarily above zero.
            Assert.Equal(0, pool.RetainedPages);
            pool.Return(rejected);
            Assert.Empty(rejected.Array); // New reservations use the current zero budget.
            Assert.Equal(PageSize, pool.RetainedBytes);
            Assert.Equal(0, pool.RetainedPages);
        }
        finally
        {
            publish.TrySetResult();
            await returning.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }

        Assert.Empty(pending.Array); // Publication must trigger another trim; the earlier setter could not see this page.
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
    public async Task StaleOverflowTrim_AfterIncrease_PreservesAllFreePages()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var pool = new ArcBufferPagePool(2 * PageSize);
        var pending = pool.Rent();
        var other = pool.Rent();
        var pendingArray = pending.Array;
        var otherArray = other.Array;
        Assert.True(pool.TryReserve(pending.Array.Length));
        pool.MaxRetainedBytes = 0; // No published page can be trimmed yet, leaving an overflow request to repair.
        var overflowObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var trim = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var trimming = Task.Run(async () =>
        {
            Assert.True(pool.RetainedBytes > pool.MaxRetainedBytes);
            overflowObserved.SetResult();
            await trim.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            pool.Trim(); // The same slow path used by Publish, now acting on an obsolete overflow observation.
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
    public async Task ConcurrentRentReturnAndBudgetChanges_PreserveOwnershipAndExactAccounting()
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
                else
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

                Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(30), cancellationToken), $"Worker {worker}, round {round}: end barrier timed out.");
                // No rents, returns, or setters can run until all workers pass the next start barrier.
                Assert.True(pool.RetainedBytes <= pool.MaxRetainedBytes, "The settled pool exceeded its configured budget.");
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
