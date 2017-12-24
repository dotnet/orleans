using Microsoft.Extensions.Logging;
using Orleans.Threading;

namespace Orleans.Runtime
{
    // todo: too much information about impl in the name
    internal abstract class SingleTaskAsynchAgent : AsynchAgent
    {
        protected SingleTaskAsynchAgent(string nameSuffix, ExecutorService executorService, ILoggerFactory loggerFactory) : base(nameSuffix, executorService, loggerFactory)
        {
        }

        protected SingleTaskAsynchAgent(ExecutorService executorService, ILoggerFactory loggerFactory) : base(executorService, loggerFactory)
        {
        }

        public override void OnStart()
        {
            executor.QueueWorkItem(_ => Run());
        }

        protected abstract void Run();

        protected override ThreadPoolExecutorOptions ExecutorOptions =>
            new ThreadPoolExecutorOptions(Name, GetType(), Cts.Token, loggerFactory, faultHandler: ExecutorFaultHandler);
    }
}