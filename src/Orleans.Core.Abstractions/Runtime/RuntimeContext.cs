using System;
using System.Runtime.CompilerServices;

namespace Orleans.Runtime
{
    internal class RuntimeContext
    {
        public IGrainContext GrainContext { get; private set; }

        public long? CallChainId { get; private set; }

        [ThreadStatic]
        private static RuntimeContext threadLocalContext;

        public static RuntimeContext Current => threadLocalContext;

        internal static IGrainContext CurrentGrainContext { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => threadLocalContext?.GrainContext; }

        internal static long? CurrentCallChainId { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => threadLocalContext?.CallChainId; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void SetExecutionContext(IGrainContext newContext, long? newCallChainId)
        {
            var ctx = threadLocalContext ??= new RuntimeContext();
            ctx.GrainContext = newContext;
            ctx.CallChainId = newCallChainId;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void SetExecutionContext(IGrainContext newContext, long? newCallChainId, out IGrainContext existingContext, out long? existingCallChainId)
        {
            var ctx = threadLocalContext ??= new RuntimeContext();
            existingContext = ctx.GrainContext;
            existingCallChainId = ctx.CallChainId;
            ctx.GrainContext = newContext;
            ctx.CallChainId = newCallChainId;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void ResetExecutionContext()
        {
            threadLocalContext.GrainContext = null;
        }

        public override string ToString() => $"RuntimeContext: GrainContext={GrainContext?.ToString() ?? "null"}";
    }
}
