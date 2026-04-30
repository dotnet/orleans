#nullable enable
using System.Collections.Generic;
using System.Threading;

namespace Orleans.Runtime
{
    /// <summary>
    /// A thread-local object pool for <see cref="CallbackData"/> instances.
    /// </summary>
    internal static class CallbackDataPool
    {
        private static readonly ThreadLocal<Stack<CallbackData>> _callbacks = new(() => new());

        /// <summary>
        /// The maximum number of callbacks to keep per thread.
        /// </summary>
        public static int MaxPoolSizePerThread { get; set; } = 128;

        /// <summary>
        /// Gets a callback from the pool, or creates a new one if the pool is empty.
        /// </summary>
        public static CallbackData Get()
        {
            var stack = _callbacks.Value!;
            if (stack.TryPop(out var callback))
            {
                return callback;
            }

            return new CallbackData();
        }

        /// <summary>
        /// Returns a callback to the pool via the reference counting system.
        /// This decrements the owner's reference count. The callback will be returned
        /// to the pool when all leases have been released.
        /// </summary>
        /// <param name="owner">The owner of the callback.</param>
        public static void Return(CallbackDataOwner owner)
        {
            owner.Release();
        }

        /// <summary>
        /// Internal method called by <see cref="CallbackData.ReleaseLease"/> when ref count reaches zero.
        /// Actually returns the callback to the pool after resetting it.
        /// </summary>
        internal static void ReturnCore(CallbackData callback)
        {
            callback.Reset();

            var stack = _callbacks.Value!;
            if (stack.Count < MaxPoolSizePerThread)
            {
                stack.Push(callback);
            }
        }
    }
}
