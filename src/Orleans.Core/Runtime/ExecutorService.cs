using System;
using System.Collections.Generic;
using System.Threading;
using Orleans.Messaging;

namespace Orleans.Runtime
{
    internal interface IExecutor
    {
        void QueueWorkItem(WaitCallback callback, object state = null);

        int WorkQueueCount { get; }
    }

    internal interface IStageAttribute { }

    internal interface IQueueDrainable : IStageAttribute { }

    internal class ExecutorService
    {
        public IExecutor GetExecutor(GetExecutorRequest request)
        {
            var stageType = request.StageType;
            if (typeof(SingleTaskAsynchAgent).IsAssignableFrom(stageType))
            {
                return new ThreadPerTaskExecutor(request.StageName);
            }
            
            return new QueuedExecutor(
                request.StageName,
                request.CancellationTokenSource,
                typeof(IQueueDrainable).IsAssignableFrom(stageType));
        }
    }

    internal class GetExecutorRequest
    {
        public GetExecutorRequest(Type stageType, string stageName, CancellationTokenSource cts)
        {
            StageType = stageType;
            StageName = stageName;
            CancellationTokenSource = cts;
        }

        public Type StageType { get; }

        public string StageName { get; }

        public CancellationTokenSource CancellationTokenSource { get; }
    }
}