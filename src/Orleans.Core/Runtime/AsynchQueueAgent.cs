using System.Threading;
using Microsoft.Extensions.Logging;
using Orleans.Threading;

namespace Orleans.Runtime
{
    internal abstract class AsynchQueueAgent<T> : AsynchAgent
    {
        protected AsynchQueueAgent(string nameSuffix, ExecutorService executorService, ILoggerFactory loggerFactory)
            : base(nameSuffix, executorService, loggerFactory)
        {
            ProcessAction = state => Process((T)state);
        }

        public WaitCallback ProcessAction { get; }

        public void QueueRequest(T request)
        {
            if (State != ThreadState.Running)
            {
                Log.LogWarning($"Invalid usage attempt of {Name} agent in {State.ToString()} state");
                return;
            }

            OnEnqueue(request);
            executor.QueueWorkItem(ProcessAction, request);
        }

        public int Count => executor.WorkQueueCount;

        protected abstract void Process(T request);

        protected virtual bool DrainAfterCancel { get; } = false;

        protected virtual void OnEnqueue(T request) { }

        protected override ThreadPoolExecutorOptions.Builder ExecutorOptionsBuilder => base.ExecutorOptionsBuilder
            .WithDrainAfterCancel(DrainAfterCancel);

        //  trackQueueStatistic, from OrleansTaskScheduler todo: add? 
    }
}
