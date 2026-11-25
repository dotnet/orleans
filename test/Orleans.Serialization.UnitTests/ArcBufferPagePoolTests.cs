using System;
using System.Collections.Concurrent;
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
    public void PinnedPages_ReuseThePageAndBackingArray()
    {
        var pool = new ArcBufferPagePool();
        var page = pool.Rent();
        var array = page.Array;
        Assert.Equal(PageSize, array.Length);
        Assert.True(page.IsMinimumSize);

        pool.Return(page);

        Assert.Equal(1, pool.RetainedPages);
        Assert.Same(page, pool.Rent());
        Assert.Same(array, page.Array);
        Assert.Equal(0, pool.RetainedPages);
        pool.Return(page);
        pool.Evict();
        Assert.Equal(1, pool.RetainedPages);
        pool.Evict();
        Assert.Equal(0, pool.RetainedPages);
        Assert.Empty(page.Array);
    }

    [Theory]
    [InlineData(2 * PageSize)]
    [InlineData(1024 * 1024)]
    [InlineData(2 * 1024 * 1024)]
    public void LargePages_ReturnToArrayPoolWithoutEnteringThePinnedPageCache(int size)
    {
        var pool = new ArcBufferPagePool();
        var minimum = pool.Rent();
        var large = pool.Rent(size);
        var backingArray = large.Array;
        Assert.Equal(size, backingArray.Length);

        pool.Return(minimum);
        pool.Return(large);

        Assert.Empty(large.Array);
        Assert.Equal(1, pool.RetainedPages);
        Assert.Same(minimum, pool.Rent());
        Assert.Equal(0, pool.RetainedPages);
        pool.Return(minimum);
        pool.Evict();
        pool.Evict();
        Assert.Empty(minimum.Array);
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

        // Initial misses represent growing demand and preserve the newly returned working set.
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
        Assert.Equal(200 - rents + returns - expectedPages, pages.Count(static page => page.Array.Length == 0));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(200)]
    public void Eviction_RepeatedIdleTicksDrainTheCache(int count)
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
        }

        pool.Evict();
        Assert.Equal(0, pool.RetainedPages);
        Assert.All(pages, static page => Assert.Empty(page.Array));
    }

    [Fact]
    public void Eviction_SteadySmallWorkingSetReleasesAtLeastOnePage()
    {
        var pool = new ArcBufferPagePool();
        var pages = Enumerable.Range(0, 20).Select(_ => pool.Rent()).ToArray();
        foreach (var page in pages)
        {
            pool.Return(page);
        }

        pool.Evict();
        var rented = pool.Rent();
        pool.Return(rented);

        pool.Evict();

        Assert.Equal(19, pool.RetainedPages);
        Assert.Empty(pages[1].Array);
        Assert.Equal(PageSize, rented.Array.Length);
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
        Assert.All(pages[..10], static page => Assert.Empty(page.Array));
    }

    [Theory]
    [InlineData(PageSize)]
    [InlineData(2 * PageSize)]
    public void Eviction_PreservesActivePinnedSlices(int size)
    {
        var pool = new ArcBufferPagePool();
        var active = pool.Rent(size);
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

        Assert.Equal(0, pool.RetainedPages);
        Assert.All(pages, static page => Assert.Empty(page.Array));
        Assert.Equal(1, active.ReferenceCount);
        Assert.Equal(token, active.Version);
        Assert.Equal(new byte[] { 42 }, reader.ToArray());
    }

    [Fact]
    public void Eviction_AcceptsReturnedPagesAfterTheCacheDrains()
    {
        var pool = new ArcBufferPagePool();
        var published = pool.Rent();
        var pending = pool.Rent();
        pool.Return(published);
        pool.Evict();
        pool.Evict();
        Assert.Empty(published.Array);
        Assert.Equal(PageSize, pending.Array.Length);
        Assert.Equal(0, pool.RetainedPages);

        pool.Return(pending);
        pool.Evict();
        Assert.Equal(1, pool.RetainedPages);
        pool.Evict();
        Assert.Equal(0, pool.RetainedPages);
        Assert.Empty(pending.Array);
    }

    [Fact]
    public async Task ConcurrentRentReturnAndEviction_PreserveExclusiveOwnership()
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
                Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(30), cancellationToken),
                    $"Worker {worker}, round {round}: start barrier timed out.");
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
                        pool.Evict();
                    }
                }

                Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(30), cancellationToken),
                    $"Worker {worker}, round {round}: end barrier timed out.");
            }
        }, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
        Assert.Empty(activePages);
        Assert.Empty(activeArrays);
        for (var tick = 0; tick < 100 && pool.RetainedPages > 0; tick++)
        {
            pool.Evict();
        }

        Assert.Equal(0, pool.RetainedPages);
        var subsequent = pool.Rent();
        pool.Return(subsequent);
        Assert.Same(subsequent, pool.Rent());
        Assert.Equal(PageSize, subsequent.Array.Length);
    }
}
