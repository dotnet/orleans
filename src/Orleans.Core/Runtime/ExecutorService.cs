using System;
using System.Threading;

namespace Orleans.Runtime
{
    internal interface IExecutor
    {
        void QueueWorkItem(WaitCallback callBack, object state = null);

        int WorkQueueLength { get; }
    }

    internal class ExecutorService
    {
        public IExecutor GetExecutor(GetExecutorRequest getExecutorRequest)
        {
            if (typeof(SingleTaskAsynchAgent).IsAssignableFrom(getExecutorRequest.StageType))
            {
                return new ThreadPerTaskExecutor(getExecutorRequest.Name);
            }

            return new QueuedExecutor(getExecutorRequest.Name, getExecutorRequest.CancellationTokenSource);
        }
    }

    internal class GetExecutorRequest
    {
        public GetExecutorRequest(Type stageType, string name, CancellationTokenSource cts)
        {
            StageType = stageType;
            Name = name;
            CancellationTokenSource = cts;
        }

        public Type StageType { get; }

        public string Name { get; }

        public CancellationTokenSource CancellationTokenSource { get; }
    }
}