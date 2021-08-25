
using Microsoft.Extensions.Logging;

namespace Orleans
{
    internal class ClusterClientLifecycle : LifecycleSubject, IClusterClientLifecycle
    {
        public ClusterClientLifecycle(ILogger<ClusterClientLifecycle> logger) : base(logger)
        {
        }
    }
}