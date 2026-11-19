using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Orleans.Runtime.Diagnostics;

// Resource ownership diagnostics are separate from the public grain lifecycle sequence.
internal static class ActivationTimeoutEvents
{
    internal const string ListenerName = "Orleans.ActivationTimeout";
    private static readonly DiagnosticListener Listener = new(ListenerName);

    internal sealed class Scheduled(
        IGrainContext grainContext,
        CancellationToken callerToken,
        CancellationToken timeoutToken)
    {
        public readonly IGrainContext GrainContext = grainContext;
        public readonly CancellationToken CallerToken = callerToken;
        public readonly CancellationToken TimeoutToken = timeoutToken;
    }

    internal static void EmitScheduled(
        IGrainContext grainContext, CancellationToken callerToken, CancellationToken timeoutToken)
    {
        if (!Listener.IsEnabled(nameof(Scheduled))) return;
        Emit(grainContext, callerToken, timeoutToken);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Emit(IGrainContext grainContext, CancellationToken callerToken, CancellationToken timeoutToken)
            => Listener.Write(nameof(Scheduled), new Scheduled(grainContext, callerToken, timeoutToken));
    }
}
