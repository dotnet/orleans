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
        public static Response<T> Get<T>() => Pool<Response<T>>.Instance.Get();

        /// <summary>
        /// Returns a value to the pool.
        /// </summary>
        /// <typeparam name="T">The underlying response type.</typeparam>
        /// <param name="obj">The value to return to the pool.</param>
        public static void Return<T>(Response<T> obj) => Pool<Response<T>>.Instance.Return(obj);

        /// <summary>
        /// Rents a generated concrete response holder.
        /// </summary>
        /// <typeparam name="TResponse">The concrete response type.</typeparam>
        /// <returns>A reset response holder.</returns>
        public static TResponse GetGenerated<TResponse>() where TResponse : Response, new() => Pool<TResponse>.Instance.Get();

        /// <summary>
        /// Returns a generated response after its value and provider dependencies have been cleared.
        /// </summary>
        /// <typeparam name="TResponse">The concrete response type.</typeparam>
        /// <param name="response">The reset response.</param>
        public static void ReturnGenerated<TResponse>(TResponse response) where TResponse : Response, new() => Pool<TResponse>.Instance.Return(response);

        private static class Pool<TResponse> where TResponse : Response, new()
        {
            public static readonly ConcurrentObjectPool<TResponse> Instance = new();
        }
    }
}
