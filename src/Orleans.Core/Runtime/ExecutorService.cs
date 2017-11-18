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

    internal class ExecutorService
    {
        public IExecutor GetExecutor(GetExecutorRequest request)
        {
            if (typeof(GatewayConnection).IsAssignableFrom(request.StageType))
            {
                // After stop GatewayConnection needs to reroute not yet sent messages to another gateway 
                return ConstructQueuedExecutor()(true);
            }

            if (typeof(SingleTaskAsynchAgent).IsAssignableFrom(request.StageType))
            {
                return new ThreadPerTaskExecutor(request.StageName);
            }

            return ConstructQueuedExecutor()(false);

            Func<bool, QueuedExecutor> ConstructQueuedExecutor()
            {
                return drainAfterCancel => new QueuedExecutor(request.StageName, request.CancellationTokenSource, drainAfterCancel);
            }
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