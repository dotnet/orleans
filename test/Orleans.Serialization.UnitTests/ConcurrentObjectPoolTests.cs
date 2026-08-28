using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Extensions.ObjectPool;
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
    public void PoolInstancesOfSameTypeAreIsolatedAndUseTheirOwnPolicies()
    {
        var firstState = new PolicyState(1);
        var secondState = new PolicyState(2);
        using var firstPool = new ConcurrentObjectPool<PooledItem, TrackingPolicy>(new(firstState));
        using var secondPool = new ConcurrentObjectPool<PooledItem, TrackingPolicy>(new(secondState));

        var firstItem = firstPool.Get();
        var secondItem = secondPool.Get();
        firstPool.Return(firstItem);
        secondPool.Return(secondItem);

        Assert.Equal(1, firstItem.PoolId);
        Assert.Equal(2, secondItem.PoolId);
        Assert.Same(firstItem, firstPool.Get());
        Assert.Same(secondItem, secondPool.Get());
        Assert.Equal(1, firstState.Created);
        Assert.Equal(1, secondState.Created);
        Assert.Equal(1, firstState.Returned);
        Assert.Equal(1, secondState.Returned);
    }

    [Fact]
    public void NestedRentsDoNotReturnTheSameInstance()
    {
        using var pool = new ConcurrentObjectPool<PooledItem>();

        var first = pool.Get();
        var second = pool.Get();

        Assert.NotSame(first, second);

        pool.Return(first);
        pool.Return(second);

        Assert.Same(second, pool.Get());
        Assert.Same(first, pool.Get());
    }

    [Fact]
    public void CrossThreadReturnStoresTheItemOnTheReturningThread()
    {
        using var pool = new ConcurrentObjectPool<PooledItem>();
        var item = pool.Get();
        PooledItem? returnedItem = null;
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                pool.Return(item);
                returnedItem = pool.Get();
            }
            catch (Exception error)
            {
                exception = error;
            }
        });

        thread.Start();

        Assert.True(thread.Join(Timeout));
        Assert.Null(exception);
        Assert.Same(item, returnedItem);
        Assert.NotSame(item, pool.Get());
    }

    [Fact]
    public void MaxPoolSizeBoundsEachThreadStack()
    {
        var state = new PolicyState(1);
        using var pool = new ConcurrentObjectPool<PooledItem, TrackingPolicy>(new(state))
        {
            MaxPoolSize = 1
        };
        var first = pool.Get();
        var second = pool.Get();

        pool.Return(first);
        pool.Return(second);

        Assert.Same(first, pool.Get());
        Assert.NotSame(second, pool.Get());
        Assert.Equal(3, state.Created);
        Assert.Equal(2, state.Returned);
    }

    [Fact]
    public void PolicyExceptionsDoNotCorruptPoolState()
    {
        var state = new PolicyState(1) { ThrowOnNextCreate = true };
        using var pool = new ConcurrentObjectPool<PooledItem, TrackingPolicy>(new(state));

        Assert.Throws<InvalidOperationException>(() => pool.Get());

        var item = pool.Get();
        state.ThrowOnNextReturn = true;

        Assert.Throws<InvalidOperationException>(() => pool.Return(item));

        pool.Return(item);

        Assert.Same(item, pool.Get());
        Assert.Equal(2, state.Created);
        Assert.Equal(2, state.Returned);
    }

    [Fact]
    public void DisposedPoolRejectsRentsAndDiscardsReturns()
    {
        var state = new PolicyState(1);
        var pool = new ConcurrentObjectPool<PooledItem, TrackingPolicy>(new(state));
        var item = pool.Get();

        pool.Dispose();
        pool.Return(item);

        Assert.Throws<ObjectDisposedException>(() => pool.Get());
        Assert.Equal(1, state.Returned);
    }

    [Fact]
    public void MultiplePoolInstancesRemainIsolatedUnderContention()
    {
        const int poolCount = 4;
        const int threadCount = 8;
        const int iterationCount = 10_000;
        var states = new PolicyState[poolCount];
        var pools = new ConcurrentObjectPool<PooledItem, TrackingPolicy>[poolCount];
        for (var i = 0; i < poolCount; i++)
        {
            states[i] = new(i);
            pools[i] = new(new(states[i]));
        }

        var exceptions = new ConcurrentQueue<Exception>();
        var threads = new Thread[threadCount];
        for (var threadIndex = 0; threadIndex < threads.Length; threadIndex++)
        {
            var offset = threadIndex;
            threads[threadIndex] = new Thread(() =>
            {
                try
                {
                    for (var iteration = 0; iteration < iterationCount; iteration++)
                    {
                        var poolIndex = (iteration + offset) % poolCount;
                        var item = pools[poolIndex].Get();
                        if (item.PoolId != poolIndex)
                        {
                            throw new InvalidOperationException($"Expected pool {poolIndex}, got {item.PoolId}.");
                        }

                        pools[poolIndex].Return(item);
                    }
                }
                catch (Exception exception)
                {
                    exceptions.Enqueue(exception);
                }
            });
            threads[threadIndex].Start();
        }

        foreach (var thread in threads)
        {
            Assert.True(thread.Join(Timeout));
        }

        foreach (var pool in pools)
        {
            pool.Dispose();
        }

        Assert.Empty(exceptions);
        Assert.All(states, state => Assert.Equal(threadCount, state.Created));
        Assert.All(states, state => Assert.Equal(threadCount * iterationCount / poolCount, state.Returned));
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
            Assert.True(itemReturned.Wait(Timeout, TestContext.Current.CancellationToken));
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

    [Fact]
    public void ShortLivedPoolsAndThreadsDoNotRetainState()
    {
        var references = CreateWeakReferencesFromShortLivedThreads(64);

        Collect();

        Assert.All(references, reference => Assert.False(reference.IsAlive));
    }

    [Fact]
    public void CollectibleGenericTypesAreNotRootedByLiveThreadStorage()
    {
        var references = CreateWeakReferencesToCollectiblePoolState();

        Collect();

        Assert.All(references, reference => Assert.False(reference.IsAlive));
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] CreateWeakReferencesFromShortLivedThreads(int threadCount)
    {
        var references = new WeakReference[threadCount * 2];
        var threads = new Thread[threadCount];
        for (var i = 0; i < threadCount; i++)
        {
            var index = i;
            threads[i] = new Thread(() =>
            {
                var pool = new ConcurrentObjectPool<RetainedItem>();
                var item = new RetainedItem();
                pool.Return(item);
                references[index * 2] = new(pool);
                references[index * 2 + 1] = new(item);
            });
            threads[i].Start();
        }

        foreach (var thread in threads)
        {
            Assert.True(thread.Join(Timeout));
        }

        return references;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] CreateWeakReferencesToCollectiblePoolState()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName($"CollectiblePoolTypes_{Guid.NewGuid():N}"),
            AssemblyBuilderAccess.RunAndCollect);
        var module = assembly.DefineDynamicModule("CollectiblePoolTypes");
        var typeBuilder = module.DefineType("PooledItem", TypeAttributes.Public | TypeAttributes.Class);
        typeBuilder.DefineDefaultConstructor(MethodAttributes.Public);
        var itemType = typeBuilder.CreateType();
        var poolType = typeof(ConcurrentObjectPool<>).MakeGenericType(itemType);
        var pool = Activator.CreateInstance(poolType)!;
        var item = poolType.GetMethod(nameof(Microsoft.Extensions.ObjectPool.ObjectPool<object>.Get))!.Invoke(pool, null)!;
        poolType.GetMethod(nameof(Microsoft.Extensions.ObjectPool.ObjectPool<object>.Return))!.Invoke(pool, [item]);
        return [new(itemType), new(pool), new(item)];
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
        public int PoolId { get; init; }
    }

    private sealed class RetainedItem
    {
        private readonly byte[] _payload = new byte[64 * 1024];
    }

    private sealed class CallbackPooledItem(Action<CallbackPooledItem> onDisposed) : IDisposable
    {
        public void Dispose() => onDisposed(this);
    }

    private readonly struct CallbackPoolPolicy(Action<CallbackPooledItem> onDisposed) : IPooledObjectPolicy<CallbackPooledItem>
    {
        public CallbackPooledItem Create() => new(onDisposed);

        public bool Return(CallbackPooledItem obj) => true;
    }

    private readonly struct TrackingPolicy(PolicyState state) : IPooledObjectPolicy<PooledItem>
    {
        public PooledItem Create() => state.Create();

        public bool Return(PooledItem obj) => state.Return(obj);
    }

    private sealed class PolicyState(int poolId)
    {
        private int _created;
        private int _returned;

        public int Created => Volatile.Read(ref _created);

        public int Returned => Volatile.Read(ref _returned);

        public bool ThrowOnNextCreate { get; set; }

        public bool ThrowOnNextReturn { get; set; }

        public PooledItem Create()
        {
            Interlocked.Increment(ref _created);
            if (ThrowOnNextCreate)
            {
                ThrowOnNextCreate = false;
                throw new InvalidOperationException("Create failed.");
            }

            return new() { PoolId = poolId };
        }

        public bool Return(PooledItem item)
        {
            Interlocked.Increment(ref _returned);
            if (ThrowOnNextReturn)
            {
                ThrowOnNextReturn = false;
                throw new InvalidOperationException("Return failed.");
            }

            Assert.Equal(poolId, item.PoolId);
            return true;
        }
    }
}
