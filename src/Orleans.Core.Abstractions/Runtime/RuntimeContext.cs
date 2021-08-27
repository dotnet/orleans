using System;
using System.Runtime.CompilerServices;

namespace Orleans.Runtime
{
    internal static class RuntimeContext
    {
        [ThreadStatic]
        private static IGrainContext _threadLocalContext;

        public static IGrainContext Current => _threadLocalContext;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void SetExecutionContext(IGrainContext newContext)
        {
            _threadLocalContext = newContext;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void SetExecutionContext(IGrainContext newContext, out IGrainContext existingContext)
        {
            existingContext = _threadLocalContext;
            _threadLocalContext = newContext;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void ResetExecutionContext()
        {
            _threadLocalContext = null;
        }
    }
}
