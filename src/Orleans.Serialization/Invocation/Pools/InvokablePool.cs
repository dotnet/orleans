#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Orleans.Serialization.Invocation
{
    /// <summary>
    /// Thread-local object pool for <see cref="IInvokable"/> implementations.
    /// Registered as an open generic singleton and injected into generated activators.
    /// </summary>
    /// <typeparam name="T">The invokable type.</typeparam>
    public sealed class InvokablePool<T> where T : class, IInvokable
    {
        private const int MaxPoolSizePerThread = 128;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Stack<T> GetStack() => PerThreadStack.Stack ??= new();

        /// <summary>
        /// Tries to get an instance from the pool.
        /// </summary>
        /// <param name="item">The pooled item, if available.</param>
        /// <returns>True if an item was retrieved from the pool; false if the pool was empty.</returns>
        public bool TryGet([NotNullWhen(true)] out T? item)
        {
            var stack = GetStack();
            return stack.TryPop(out item);
        }

        /// <summary>
        /// Returns an instance to the pool.
        /// </summary>
        /// <param name="item">The item to return.</param>
        public void Return(T item)
        {
            var stack = GetStack();
            if (stack.Count < MaxPoolSizePerThread)
            {
                stack.Push(item);
            }
        }

        // Nested class to hold ThreadStatic field per generic type instantiation
        private static class PerThreadStack
        {
            [ThreadStatic]
            internal static Stack<T>? Stack;
        }
    }

    /// <summary>
    /// Static helper for <see cref="IInvokable"/> pooling.
    /// </summary>
    /// <remarks>
    /// This is kept for backward compatibility. New code should use <see cref="InvokablePool{T}"/> directly.
    /// </remarks>
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
