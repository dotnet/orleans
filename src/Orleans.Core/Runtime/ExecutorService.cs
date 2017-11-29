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
        public IExecutor GetExecutor(GetThreadPoolExecutorRequest request)
        {
            var stageType = request.StageType;
            if (typeof(SingleTaskAsynchAgent).IsAssignableFrom(stageType))
            {
                return new ThreadPerTaskExecutor(request.StageName);
            }
            
            return new ThreadPoolExecutor(
                request.StageName,
                request.CancellationToken,
                typeof(IQueueDrainable).IsAssignableFrom(stageType));
        }

        public IExecutor GetThreadpPerTaskExecutor(GetExecutorRequest request)
        {
            return new ThreadPerTaskExecutor(request.StageName);
        }
    }

    internal class GetThreadPoolExecutorRequest : GetExecutorRequest
    {
        public GetThreadPoolExecutorRequest(Type stageType, string stageName, CancellationToken ct) 
            : base(stageType, stageName)
        {
            CancellationToken = ct;
        }

        public CancellationToken CancellationToken { get; }
    }

    internal class GetExecutorRequest
    {
        public GetExecutorRequest(Type stageType, string stageName)
        {
            StageType = stageType;
            StageName = stageName;
        }

        public Type StageType { get; }

        public string StageName { get; }
    }
}