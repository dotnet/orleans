using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    internal abstract class DedicatedAsynchAgent : AsynchAgent
    {
        protected DedicatedAsynchAgent(string nameSuffix, ExecutorService executorService, ILoggerFactory loggerFactory) : base(nameSuffix, executorService, loggerFactory)
        {
        }

        protected DedicatedAsynchAgent(ExecutorService executorService, ILoggerFactory loggerFactory) : base(executorService, loggerFactory)
        {
        }

        public override void OnStart()
        {
            executor.QueueWorkItem(_ => Run());
        }

        protected abstract void Run();
    }
}