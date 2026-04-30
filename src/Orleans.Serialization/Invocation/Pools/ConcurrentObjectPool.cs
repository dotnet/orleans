#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.ObjectPool;

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
        private readonly TPoolPolicy _policy;

        public ConcurrentObjectPool(TPoolPolicy policy) => _policy = policy;

        public int MaxPoolSize { get; set; } = int.MaxValue;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Stack<T> GetStack() => PerThreadStack.Stack ??= new();

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

        // Nested class to hold ThreadStatic field per generic type instantiation
        private static class PerThreadStack
        {
            [ThreadStatic]
            internal static Stack<T>? Stack;
        }
    }
}
