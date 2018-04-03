
using Microsoft.Extensions.Logging;

namespace Orleans
{
    internal class ClusterClientLifecycle : LifecycleSubject, IClusterClientLifecycle
    {
        public ClusterClientLifecycle(ILogger<LifecycleSubject> logger) : base(logger)
        {
        }
    }
}