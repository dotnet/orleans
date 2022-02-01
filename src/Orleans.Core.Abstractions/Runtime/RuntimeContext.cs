using System;
using System.Runtime.CompilerServices;

namespace Orleans.Runtime
{
    /// <summary>
    /// Functionality for managing the current grain context.
    /// </summary>
    internal static class RuntimeContext
    {
        /// <summary>
        /// The thread-local context.
        /// </summary>
        [ThreadStatic]
        private static IGrainContext _threadLocalContext;

        /// <summary>
        /// Gets the current grain context.
        /// </summary>
        public static IGrainContext Current => _threadLocalContext;

        /// <summary>
        /// Sets the current grain context.
        /// </summary>
        /// <param name="newContext">The new context.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void SetExecutionContext(IGrainContext newContext)
        {
            _threadLocalContext = newContext;
        }

        /// <summary>
        /// Sets the current grain context.
        /// </summary>
        /// <param name="newContext">The new context.</param>
        /// <param name="existingContext">The existing context.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void SetExecutionContext(IGrainContext newContext, out IGrainContext existingContext)
        {
            existingContext = _threadLocalContext;
            _threadLocalContext = newContext;
        }

        /// <summary>
        /// Resets the current grain context.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void ResetExecutionContext()
        {
            _threadLocalContext = null;
        }
    }
}
