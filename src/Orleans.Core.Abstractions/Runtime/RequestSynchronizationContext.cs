using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Orleans.Runtime
{
    /// <summary>
    /// Implements a per-request <see cref="SynchronizationContext"/>, used to propagate runtime information such as the current
    /// <see cref="IGrainContext"/> and request message along the call.
    /// </summary>
    public sealed class RequestSynchronizationContext : OrleansSynchronizationContext
    {
        public RequestSynchronizationContext(OrleansSynchronizationContext inner)
        {
            if (inner is RequestSynchronizationContext)
            {
                ThrowInvalidArgumentException();
            }

            InnerContext = inner;
        }

        public OrleansSynchronizationContext InnerContext { get; init; }

        private object _currentRequest;
        public override object CurrentRequest { get => _currentRequest; set => _currentRequest = value; }

        public override IGrainContext GrainContext => InnerContext.GrainContext;

        public override void Send(SendOrPostCallback callback, object state)
        {
            InnerContext.Send(callback, state);
        }

        public override void Post(SendOrPostCallback callback, object state)
        {
            InnerContext.Schedule(callback, state, this);
        }
        public override void Schedule(SendOrPostCallback callback, object state, OrleansSynchronizationContext context) => InnerContext.Schedule(callback, state, context);

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowInvalidArgumentException() => throw new ArgumentException();

        /// <inheritdoc/>
        public override RequestSynchronizationContext CreateCopy()
        {
            return new RequestSynchronizationContext(InnerContext)
            {
                CurrentRequest = CurrentRequest,
            };
        }

        public override string ToString() => $"[RequestSynchronizationContext] Inner=[{InnerContext}] Request=[{CurrentRequest}]";
    }
}
