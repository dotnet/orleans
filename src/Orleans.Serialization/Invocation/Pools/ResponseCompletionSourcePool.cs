namespace Orleans.Serialization.Invocation
{
    public static class ResponseCompletionSourcePool
    {
        internal static readonly ConcurrentObjectPool<ResponseCompletionSource, DefaultConcurrentObjectPoolPolicy<ResponseCompletionSource>> UntypedPool = new(new());

        public static ResponseCompletionSource<T> Get<T>() => TypedPool<T>.Pool.Get();
        public static void Return<T>(ResponseCompletionSource<T> obj) => TypedPool<T>.Pool.Return(obj);

        public static ResponseCompletionSource Get() => UntypedPool.Get();
        public static void Return(ResponseCompletionSource obj) => UntypedPool.Return(obj);

        private static class TypedPool<T>
        {
            public static readonly ConcurrentObjectPool<ResponseCompletionSource<T>, DefaultConcurrentObjectPoolPolicy<ResponseCompletionSource<T>>> Pool = new(new());
        }
    }
}