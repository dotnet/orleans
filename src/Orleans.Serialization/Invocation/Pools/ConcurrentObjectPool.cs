using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.ObjectPool;

#nullable disable
namespace Orleans.Serialization.Invocation
{
    internal sealed class ConcurrentObjectPool<T> : ConcurrentObjectPool<T, DefaultConcurrentObjectPoolPolicy<T>> where T : class, new()
    {
        public ConcurrentObjectPool() : base(new())
        {
        }
    }

    internal class ConcurrentObjectPool<T, TPoolPolicy> : ObjectPool<T>, IDisposable where T : class where TPoolPolicy : IPooledObjectPolicy<T>
    {
        private readonly ThreadLocal<Stack<T>> _objects = new(() => new());

        private readonly TPoolPolicy _policy;
        private int _disposed;

        public ConcurrentObjectPool(TPoolPolicy policy) => _policy = policy;

        public int MaxPoolSize { get; set; } = int.MaxValue;

        public override T Get()
        {
            ThrowIfDisposed();
            var stack = _objects.Value;
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
                    stack = _objects.Value;
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
                // Clear per-thread stacks so pooled objects do not keep their owning service provider alive.
                _objects.Dispose();
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(typeof(ConcurrentObjectPool<T, TPoolPolicy>).FullName);
            }
        }
    }
}
