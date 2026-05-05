using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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

    internal class ConcurrentObjectPool<T, TPoolPolicy> : ObjectPool<T> where T : class where TPoolPolicy : IPooledObjectPolicy<T>
    {
        private static int NextPoolId = -1;

        private readonly int _poolId = Interlocked.Increment(ref NextPoolId);
        private readonly TPoolPolicy _policy;

        public ConcurrentObjectPool(TPoolPolicy policy) => _policy = policy;

        public int MaxPoolSize { get; set; } = int.MaxValue;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Stack<T> GetStack()
        {
            var poolId = _poolId;
            var stacks = PerThreadStack.Stacks;
            if (stacks is null)
            {
                stacks = PerThreadStack.Stacks = new Stack<T>[poolId + 1];
            }
            else if ((uint)poolId >= (uint)stacks.Length)
            {
                Array.Resize(ref stacks, Math.Max(poolId + 1, stacks.Length * 2));
                PerThreadStack.Stacks = stacks;
            }

            return stacks[poolId] ??= new();
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
                var stack = GetStack();
                if (stack.Count < MaxPoolSize)
                {
                    stack.Push(obj);
                }
            }
        }

        // Thread-static stacks are indexed by pool instance to avoid sharing state between pools with different policies.
        private static class PerThreadStack
        {
            [ThreadStatic]
            internal static Stack<T>[] Stacks;
        }
    }
}
