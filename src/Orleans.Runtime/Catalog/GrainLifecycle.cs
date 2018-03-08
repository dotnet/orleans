
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    internal class GrainLifecycle : LifecycleSubject, IGrainLifecycle
    {
        public GrainLifecycle(ILogger<LifecycleSubject> logger) : base(logger)
        {
        }
    }
}
