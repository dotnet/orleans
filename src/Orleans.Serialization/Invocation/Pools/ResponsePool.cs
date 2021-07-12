using Microsoft.Extensions.ObjectPool;

namespace Orleans.Serialization.Invocation
{
    public static class ResponsePool
    {
        public static PooledResponse<T> Get<T>() => TypedPool<T>.Pool.Get();

        public static void Return<T>(PooledResponse<T> obj) => TypedPool<T>.Pool.Return(obj);

        private static class TypedPool<T>
        {
            public static readonly ConcurrentObjectPool<PooledResponse<T>> Pool = new();
        }
    }
}