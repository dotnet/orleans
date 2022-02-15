namespace Orleans.Serialization.Invocation
{
    /// <summary>
    /// Object pool for <see cref="ResponseCompletionSource"/> and <see cref="ResponseCompletionSource{TResult}"/>.
    /// </summary>
    public static class ResponseCompletionSourcePool
    {
        internal static readonly ConcurrentObjectPool<ResponseCompletionSource, DefaultConcurrentObjectPoolPolicy<ResponseCompletionSource>> UntypedPool = new(new());

        /// <summary>
        /// Gets a value from the pool.
        /// </summary>
        /// <typeparam name="T">The underlying result type.</typeparam>
        /// <returns>A value from the pool.</returns>
        public static ResponseCompletionSource<T> Get<T>() => TypedPool<T>.Pool.Get();

        /// <summary>
        /// Returns a value to the pool.
        /// </summary>
        /// <typeparam name="T">The underlying result type.</typeparam>
        /// <param name="obj">The value to return to the pool</param>
        public static void Return<T>(ResponseCompletionSource<T> obj) => TypedPool<T>.Pool.Return(obj);

        /// <summary>
        /// Gets a value from the pool.
        /// </summary>
        /// <returns>A value from the pool.</returns>
        public static ResponseCompletionSource Get() => UntypedPool.Get();

        /// <summary>
        /// Returns a value to the pool.
        /// </summary>
        /// <param name="obj">The value to return to the pool</param>
        public static void Return(ResponseCompletionSource obj) => UntypedPool.Return(obj);

        private static class TypedPool<T>
        {
            public static readonly ConcurrentObjectPool<ResponseCompletionSource<T>, DefaultConcurrentObjectPoolPolicy<ResponseCompletionSource<T>>> Pool = new(new());
        }
    }
}