using System;
using System.Runtime.CompilerServices;

namespace Orleans.Runtime
{
    internal class RuntimeContext
    {
        public IGrainContext GrainContext { get; private set; }

        [ThreadStatic]
        private static RuntimeContext threadLocalContext;

        public static RuntimeContext Current => threadLocalContext;

        internal static IGrainContext CurrentGrainContext { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => threadLocalContext?.GrainContext; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void SetExecutionContext(IGrainContext newContext)
        {
            var ctx = threadLocalContext ??= new RuntimeContext();
            ctx.GrainContext = newContext;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void SetExecutionContext(IGrainContext newContext, out IGrainContext existingContext)
        {
            var ctx = threadLocalContext ??= new RuntimeContext();
            existingContext = ctx.GrainContext;
            ctx.GrainContext = newContext;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void ResetExecutionContext()
        {
            threadLocalContext.GrainContext = null;
        }

        public override string ToString() => $"RuntimeContext: GrainContext={GrainContext?.ToString() ?? "null"}";
    }
}
