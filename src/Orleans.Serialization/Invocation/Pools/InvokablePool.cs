using Microsoft.Extensions.ObjectPool;

namespace Orleans.Serialization.Invocation
{
    /// <summary>
    /// Object pool for <see cref="IInvokable"/> implementations.
    /// </summary>
    public static class InvokablePool
    {
        /// <summary>
        /// Gets a value from the pool.
        /// </summary>
        /// <typeparam name="T">The type of the value.</typeparam>
        /// <returns>A value from the pool.</returns>
        public static T Get<T>() where T : class, IInvokable, new() => TypedPool<T>.Pool.Get();

        /// <summary>
        /// Returns a value to the pool.
        /// </summary>
        /// <typeparam name="T">The type of the value.</typeparam>
        /// <param name="obj">The value to return.</param>
        public static void Return<T>(T obj) where T : class, IInvokable, new() => TypedPool<T>.Pool.Return(obj);

        private static class TypedPool<T> where T : class, IInvokable, new()
        {
            public static readonly ConcurrentObjectPool<T> Pool = new();
        }
    }
}