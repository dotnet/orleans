using System.Threading;

namespace Orleans.Runtime
{
    internal abstract class OrleansSynchronizationContext : SynchronizationContext
    {
        public static new OrleansSynchronizationContext Current => SynchronizationContext.Current as OrleansSynchronizationContext;

        public static OrleansSynchronizationContext Fork(OrleansSynchronizationContext original)
        {
            var innerContext = original switch
            {
                RequestSynchronizationContext wrapped => wrapped.InnerContext,
                _ => original
            };

            return new RequestSynchronizationContext(innerContext)
            {
                CurrentRequest = original.CurrentRequest,
            };
        }

        public abstract object CurrentRequest { get; set; }
        public abstract IGrainContext GrainContext { get; }
        public bool IsRequestFlowSuppressed { get; set; }

        public override SynchronizationContext CreateCopy() => Fork(this);

        public abstract void Schedule(SendOrPostCallback callback, object state, OrleansSynchronizationContext context);
    }
}
