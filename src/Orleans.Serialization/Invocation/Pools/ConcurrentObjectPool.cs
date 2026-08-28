using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Extensions.ObjectPool;

namespace Orleans.Serialization.Invocation;

internal sealed class ConcurrentObjectPool<T> : ConcurrentObjectPool<T, DefaultConcurrentObjectPoolPolicy<T>> where T : class, new()
{
    public ConcurrentObjectPool() : base(new())
    {
    }
}

internal class ConcurrentObjectPool<T, TPoolPolicy> : ObjectPool<T>, IDisposable where T : class where TPoolPolicy : IPooledObjectPolicy<T>
{
    private readonly object _identity = new();
    private readonly List<WeakReference<StackHolder>> _stacks = [];
    private readonly TPoolPolicy _policy;
    private int _disposed;

    public ConcurrentObjectPool(TPoolPolicy policy) => _policy = policy;

    public int MaxPoolSize { get; set; } = int.MaxValue;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Stack<T> GetStack()
    {
        ThrowIfDisposed();

        var holderReference = PerThreadStack.CachedHolder;
        if (holderReference is null
            || !holderReference.TryGetTarget(out var holder)
            || !ReferenceEquals(holder.Identity, _identity))
        {
            var stacks = PerThreadStack.Stacks ??= new();
            holder = stacks.GetValue(this, static pool => new(pool._identity));
            if (!holder.IsRegistered)
            {
                Register(holder);
                holder.IsRegistered = true;
            }

            if (holderReference is null)
            {
                PerThreadStack.CachedHolder = new(holder);
            }
            else
            {
                holderReference.SetTarget(holder);
            }
        }

        var stack = Volatile.Read(ref holder.Stack);
        if (stack is null || Volatile.Read(ref _disposed) != 0)
        {
            holder.Release();
            throw new ObjectDisposedException(typeof(ConcurrentObjectPool<T, TPoolPolicy>).FullName);
        }

        return stack;
    }

    public override T Get()
    {
        var stack = GetStack();
        if (stack.TryPop(out var result))
        {
            return result;
        }

        return _policy.Create();
    }

    public override void Return(T obj)
    {
        if (_policy.Return(obj))
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            Stack<T> stack;
            try
            {
                stack = GetStack();
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (stack.Count < MaxPoolSize)
            {
                stack.Push(obj);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            lock (_stacks)
            {
                foreach (var stackReference in _stacks)
                {
                    if (stackReference.TryGetTarget(out var holder))
                    {
                        holder.Release();
                    }
                }

                _stacks.Clear();
            }
        }
    }

    private void Register(StackHolder holder)
    {
        lock (_stacks)
        {
            for (var i = _stacks.Count - 1; i >= 0; i--)
            {
                if (!_stacks[i].TryGetTarget(out _))
                {
                    _stacks.RemoveAt(i);
                }
            }

            _stacks.Add(new(holder));
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(typeof(ConcurrentObjectPool<T, TPoolPolicy>).FullName);
        }
    }

    private static class PerThreadStack
    {
        [ThreadStatic]
        internal static ConditionalWeakTable<ConcurrentObjectPool<T, TPoolPolicy>, StackHolder>? Stacks;

        [ThreadStatic]
        internal static WeakReference<StackHolder>? CachedHolder;
    }

    private sealed class StackHolder(object identity)
    {
        internal readonly object Identity = identity;
        internal bool IsRegistered;
        internal Stack<T>? Stack = new();

        internal void Release() => Interlocked.Exchange(ref Stack, null);
    }
}

internal sealed class WeakPoolReturner<T> where T : class
{
    private WeakReference<ObjectPool<T>>? _pool;

    internal void SetPool(ObjectPool<T> pool) => _pool = new(pool);

    internal void Return(T item)
    {
        if (_pool is { } reference && reference.TryGetTarget(out var pool))
        {
            pool.Return(item);
        }
    }
}
