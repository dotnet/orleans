namespace Orleans.Serialization.Invocation
{
    /// <summary>
    /// Creates <see cref="Response{TResult}"/> values.
    /// </summary>
    /// <remarks>
    /// Each call to <see cref="Get{T}"/> returns a new response, which the garbage collector reclaims after use.
    /// </remarks>
    public static class ResponsePool
    {
        /// <summary>
        /// Creates a new response.
        /// </summary>
        /// <typeparam name="T">The underlying response type.</typeparam>
        /// <returns>A new response.</returns>
        public static Response<T> Get<T>() => new();

        /// <summary>
        /// Accepts a response which is no longer in use. The garbage collector reclaims the response.
        /// </summary>
        /// <typeparam name="T">The underlying response type.</typeparam>
        /// <param name="obj">The response which is no longer in use.</param>
        public static void Return<T>(Response<T> obj)
        {
        }
    }
}
