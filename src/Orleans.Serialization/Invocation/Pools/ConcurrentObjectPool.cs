using System.Collections.Generic;
using System.Threading;
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
        private readonly ThreadLocal<Stack<T>> _objects = new(() => new());

        private readonly TPoolPolicy _policy;

        public ConcurrentObjectPool(TPoolPolicy policy) => _policy = policy;

        public int MaxPoolSize { get; set; } = int.MaxValue;

        public override T Get()
        {
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
                var stack = _objects.Value;
                if (stack.Count < MaxPoolSize)
                {
                    stack.Push(obj);
                }
            }
        }
    }

#if !NETCOREAPP3_1_OR_GREATER
    internal static class StackExtensions
    {
        public static bool TryPop<T>(this Stack<T> stack, out T result)
        {
            if (stack.Count == 0)
            {
                result = default;
                return false;
            }

            result = stack.Pop();
            return true;
        }
    }
#endif
}