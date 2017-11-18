
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    internal class SiloLifecycle : LifecycleObservable, ISiloLifecycle
    {
        public SiloLifecycle(ILoggerFactory loggerFactory) : base(loggerFactory)
        {
        }
    }
}
