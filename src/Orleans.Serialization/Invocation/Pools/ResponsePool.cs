namespace Orleans.Serialization.Invocation
{
    /// <summary>
    /// Object pool for <see cref="Response{TResult}"/> values.
    /// </summary>
    public static class ResponsePool
    {
        /// <summary>
        /// Gets a value from the pool.
        /// </summary>
        /// <typeparam name="T">The underlying response type.</typeparam>
        /// <returns>A value from the pool.</returns>
        public static Response<T> Get<T>() => TypedPool<T>.Pool.Get();

        /// <summary>
        /// Returns a value to the pool.
        /// </summary>
        /// <typeparam name="T">The underlying response type.</typeparam>
        /// <param name="obj">The value to return to the pool.</param>
        public static void Return<T>(Response<T> obj) => TypedPool<T>.Pool.Return(obj);

        private static class TypedPool<T>
        {
            public static readonly ConcurrentObjectPool<Response<T>> Pool = new();
        }
    }
}