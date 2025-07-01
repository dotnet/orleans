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
        private static IGrainContext? _threadLocalContext;

        /// <summary>
        /// Gets the current grain context.
        /// </summary>
        public static IGrainContext? Current => _threadLocalContext;

        /// <summary>
        /// Sets the current grain context.
        /// </summary>
        /// <param name="newContext">The new context.</param>
        /// <param name="currentContext">The current context at the time of the call.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void SetExecutionContext(IGrainContext newContext, out IGrainContext? currentContext)
        {
            currentContext = _threadLocalContext;
            _threadLocalContext = newContext;
        }

        /// <summary>
        /// Resets the current grain context to the provided original context.
        /// </summary>
        /// <param name="originalContext">The original context.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void ResetExecutionContext(IGrainContext? originalContext)
        {
            _threadLocalContext = originalContext;
        }
    }
}
