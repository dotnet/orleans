using System.Threading;
using Microsoft.Extensions.Logging;
using Orleans.Threading;

namespace Orleans.Runtime
{
    internal abstract class AsynchQueueAgent<T> : AsynchAgent
    {
        private readonly QueueCounter queueCounter = new QueueCounter();

        protected AsynchQueueAgent(string nameSuffix, ExecutorService executorService, ILoggerFactory loggerFactory)
            : base(nameSuffix, executorService, loggerFactory)
        {
            ProcessAction = state => Process((T)state);
        }

        public WaitCallback ProcessAction { get; }

        public int Count => queueCounter.Count;

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

        protected abstract void Process(T request);

        protected virtual bool DrainAfterCancel { get; } = false;

        protected virtual void OnEnqueue(T request)
        {
            queueCounter.Increment();
        }

        protected override ThreadPoolExecutorOptions.Builder ExecutorOptionsBuilder => base.ExecutorOptionsBuilder
            .WithDrainAfterCancel(DrainAfterCancel)
            .WithActionFilters(queueCounter);

        protected T GetWorkItemState(Threading.ExecutionContext context)
        {
            return (T)context.WorkItem.State;
        }

        private sealed class QueueCounter : ExecutionActionFilter
        {
            private int requestsInQueueCount;

            public int Count => requestsInQueueCount;

            public override void OnActionExecuting(Threading.ExecutionContext context)
            {
                Decrement();
            }

            public void Increment()
            {
                Interlocked.Increment(ref requestsInQueueCount);
            }

            public void Decrement()
            {
                Interlocked.Decrement(ref requestsInQueueCount);
            }
        }
    }
}
