using Microsoft.Extensions.ObjectPool;

namespace Orleans.Serialization.Invocation
{
    public static class ResponseCompletionSourcePool
    {
        public static readonly DefaultObjectPool<ResponseCompletionSource> UntypedPool = new(new DefaultPooledObjectPolicy<ResponseCompletionSource>());

        public static ResponseCompletionSource<T> Get<T>() => TypedPool<T>.Pool.Get();
        public static void Return<T>(ResponseCompletionSource<T> obj) => TypedPool<T>.Pool.Return(obj);

        public static ResponseCompletionSource Get() => UntypedPool.Get();
        public static void Return(ResponseCompletionSource obj) => UntypedPool.Return(obj);

        private static class TypedPool<T>
        {
            public static readonly DefaultObjectPool<ResponseCompletionSource<T>> Pool = new(new DefaultPooledObjectPolicy<ResponseCompletionSource<T>>());
        }
    }
}