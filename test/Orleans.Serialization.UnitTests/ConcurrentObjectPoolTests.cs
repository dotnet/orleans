using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Orleans.Serialization.Invocation;
using Xunit;

namespace Orleans.Serialization.UnitTests;

[Trait("Category", "BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Serialization")]
public sealed class ConcurrentObjectPoolTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void PoolStorageIsThreadAffine()
    {
        using var pool = new ConcurrentObjectPool<PooledItem>();
        var currentThreadItem = pool.Get();
        pool.Return(currentThreadItem);

        PooledItem? otherThreadItem = null;
        var thread = new Thread(() =>
        {
            otherThreadItem = pool.Get();
            pool.Return(otherThreadItem);
        });

        thread.Start();

        Assert.True(thread.Join(Timeout));
        Assert.NotNull(otherThreadItem);
        Assert.NotSame(currentThreadItem, otherThreadItem);
        Assert.Same(currentThreadItem, pool.Get());
    }

    [Fact]
    public void DisposeReleasesObjectsStoredOnOtherThreads()
    {
        using var pool = new ConcurrentObjectPool<PooledItem>();
        using var itemReturned = new ManualResetEventSlim();
        using var releaseThread = new ManualResetEventSlim();
        WeakReference? itemReference = null;
        var thread = new Thread(() =>
        {
            itemReference = CreateAndReturnPooledItem(pool);
            itemReturned.Set();
            releaseThread.Wait();
        });

        thread.Start();

        try
        {
            Assert.True(itemReturned.Wait(Timeout));
            pool.Dispose();

            Collect();

            Assert.NotNull(itemReference);
            Assert.False(itemReference.IsAlive);
        }
        finally
        {
            releaseThread.Set();
            Assert.True(thread.Join(Timeout));
        }
    }

    [Fact]
    public void ExitedThreadDoesNotRetainPooledObjects()
    {
        using var pool = new ConcurrentObjectPool<PooledItem>();
        var itemReference = ReturnItemOnNewThread(pool);

        Collect();

        Assert.False(itemReference.IsAlive);
        GC.KeepAlive(pool);
    }

    [Fact]
    public void UndisposedPoolIsNotRootedByThreadStaticState()
    {
        var pool = CreateWeakReferenceToUndisposedPool();

        Collect();

        Assert.False(pool.IsAlive);
    }

    [Fact]
    public void CustomPolicyCallbacksDoNotRootUndisposedPool()
    {
        var pool = CreateWeakReferenceToUndisposedCallbackPool();

        Collect();

        Assert.False(pool.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ReturnItemOnNewThread(ConcurrentObjectPool<PooledItem> pool)
    {
        WeakReference? itemReference = null;
        var thread = new Thread(() => itemReference = CreateAndReturnPooledItem(pool));
        thread.Start();
        Assert.True(thread.Join(Timeout));
        return itemReference!;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateAndReturnPooledItem(ConcurrentObjectPool<PooledItem> pool)
    {
        var item = new PooledItem();
        pool.Return(item);
        return new(item);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateWeakReferenceToUndisposedPool()
    {
        var pool = new ConcurrentObjectPool<PooledItem>();
        pool.Return(new());
        return new(pool);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateWeakReferenceToUndisposedCallbackPool()
    {
        var returner = new WeakPoolReturner<CallbackPooledItem>();
        var pool = new ConcurrentObjectPool<CallbackPooledItem, CallbackPoolPolicy>(new(returner.Return));
        returner.SetPool(pool);
        pool.Get().Dispose();
        return new(pool);
    }

    private static void Collect()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }

    private sealed class PooledItem
    {
    }

    private sealed class CallbackPooledItem(Action<CallbackPooledItem> onDisposed) : IDisposable
    {
        public void Dispose() => onDisposed(this);
    }

    private readonly struct CallbackPoolPolicy(Action<CallbackPooledItem> onDisposed) : Microsoft.Extensions.ObjectPool.IPooledObjectPolicy<CallbackPooledItem>
    {
        public CallbackPooledItem Create() => new(onDisposed);

        public bool Return(CallbackPooledItem obj) => true;
    }
}
