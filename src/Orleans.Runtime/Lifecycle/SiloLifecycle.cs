
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    public class SiloLifecycle : LifecycleObservable, ISiloLifecycle
    {
        public SiloLifecycle(ILoggerFactory loggerFactory) : base(loggerFactory)
        {
        }
    }
}
