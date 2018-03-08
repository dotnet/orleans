
using Microsoft.Extensions.Logging;

namespace Orleans
{
    internal class ClusterClientLifecycle : LifecycleObservable, IClusterClientLifecycle
    {
        public ClusterClientLifecycle(ILoggerFactory loggerFactory) : base(loggerFactory)
        {
        }
    }
}