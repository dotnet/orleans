
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    internal class GrainLifecycle : LifecycleObservable, IGrainLifecycle
    {
        public GrainLifecycle(ILoggerFactory loggerFactory) : base(loggerFactory)
        {
        }
    }
}
