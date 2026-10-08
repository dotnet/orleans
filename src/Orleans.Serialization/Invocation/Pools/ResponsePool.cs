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

        /// <summary>
        /// Rents a generated concrete response holder.
        /// </summary>
        /// <typeparam name="TResponse">The concrete response type.</typeparam>
        /// <returns>A reset response holder.</returns>
        public static TResponse GetGenerated<TResponse>() where TResponse : Response, new() => GeneratedPool<TResponse>.Pool.Get();

        /// <summary>
        /// Returns a generated response after its value and provider dependencies have been cleared.
        /// </summary>
        /// <typeparam name="TResponse">The concrete response type.</typeparam>
        /// <param name="response">The reset response.</param>
        public static void ReturnGenerated<TResponse>(TResponse response) where TResponse : Response, new() => GeneratedPool<TResponse>.Pool.Return(response);

        private static class GeneratedPool<TResponse> where TResponse : Response, new()
        {
            public static readonly ConcurrentObjectPool<TResponse> Pool = new();
        }

        private static class TypedPool<T>
        {
            public static readonly ConcurrentObjectPool<Response<T>> Pool = new();
        }
    }
}
