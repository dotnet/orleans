using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
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
        
        protected override ExecutorOptions GetExecutorOptions()
        {
            return new SingleThreadExecutorOptions(Name, GetType(), Cts.Token, Log, ExecutorFaultHandler);
        }
    }
}