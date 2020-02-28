using System;
using System.Runtime.CompilerServices;

namespace Orleans.Runtime
{
    internal class RuntimeContext
    {
        public IGrainContext GrainContext { get; private set; }

        [ThreadStatic]
        private static RuntimeContext context;

        public static RuntimeContext Current => context;

        internal static IGrainContext CurrentGrainContext { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => context?.GrainContext; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void SetExecutionContext(IGrainContext shedContext)
        {
            context ??= new RuntimeContext();
            context.GrainContext = shedContext;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void ResetExecutionContext()
        {
            context.GrainContext = null;
        }

        public override string ToString() => $"RuntimeContext: GrainContext={GrainContext?.ToString() ?? "null"}";
    }
}
