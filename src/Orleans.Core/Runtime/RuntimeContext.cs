using System;
using System.Runtime.CompilerServices;

namespace Orleans.Runtime
{
    internal class RuntimeContext
    {
        public ISchedulingContext ActivationContext { get; private set; }

        [ThreadStatic]
        private static RuntimeContext context;

        public static RuntimeContext Current { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => context ??= new RuntimeContext(); }

        internal static ISchedulingContext CurrentActivationContext { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => Current?.ActivationContext; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void SetExecutionContext(ISchedulingContext shedContext)
        {
            Current.ActivationContext = shedContext;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void ResetExecutionContext()
        {
            context.ActivationContext = null;
        }

        public override string ToString() => $"RuntimeContext: ActivationContext={ActivationContext?.ToString() ?? "null"}";
    }
}
