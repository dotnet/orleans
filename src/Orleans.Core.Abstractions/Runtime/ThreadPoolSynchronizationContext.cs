using System;
using System.Threading;

namespace Orleans.Runtime
{
    internal sealed class ThreadPoolSynchronizationContext : OrleansSynchronizationContext
    {
        public ThreadPoolSynchronizationContext(IGrainContext grainContext)
        {
            GrainContext = grainContext;
        }

        public override IGrainContext GrainContext { get; }
        public override object CurrentRequest { get => default; set => throw new NotSupportedException(); }
        public override void Send(SendOrPostCallback callback, object state) => callback(state);
        public override void Post(SendOrPostCallback callback, object state) => ThreadPool.UnsafeQueueUserWorkItem(s => callback(s), state, preferLocal: true);
        public override void Schedule(SendOrPostCallback callback, object state, OrleansSynchronizationContext context) => throw new NotSupportedException();
    }
}
