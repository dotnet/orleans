using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging;
using Orleans.Messaging;

namespace Orleans.Runtime
{
    internal interface IExecutor : IHealthCheckable
    {
        void QueueWorkItem(WaitCallback callback, object state = null);

        int WorkQueueCount { get; }
    }

    internal interface IStageAttribute { }

    internal interface IQueueDrainable : IStageAttribute { }

    internal class ExecutorService
    {
        public IExecutor GetExecutor(ExecutorOptions executorOptions)
        {
            switch (executorOptions)
            {
                case ThreadPoolExecutorOptions options:
                    return new ThreadPoolExecutor(options);
                case SingleThreadExecutorOptions options:
                    return new ThreadPerTaskExecutor(options);
                default:
                    throw new NotImplementedException();
            }
        }
    }

    internal class ThreadPoolExecutorOptions : ExecutorOptions
    {
        public ThreadPoolExecutorOptions(
            Type stageType,
            string stageName,
            CancellationToken ct,
            ILogger log,
            int degreeOfParallelism = 1,
            bool drainAfterCancel = false,
            TimeSpan? workItemExecutionTimeTreshold = null,
            WorkItemStatusProvider workItemStatusProvider = null)
            : base(stageName)
        {
            StageType = stageType;
            CancellationToken = ct;
            Log = log;
            DegreeOfParallelism = degreeOfParallelism;
            DrainAfterCancel = drainAfterCancel;
            WorkItemExecutionTimeTreshold = workItemExecutionTimeTreshold ?? TimeSpan.MaxValue;
            WorkItemStatusProvider = workItemStatusProvider;
        }

        public Type StageType { get; }
        
        public CancellationToken CancellationToken { get; }

        public int DegreeOfParallelism { get; }

        public bool DrainAfterCancel { get; }

        public TimeSpan WorkItemExecutionTimeTreshold { get; }

        public WorkItemStatusProvider WorkItemStatusProvider { get; }

        public ILogger Log { get; }
    }

    internal class SingleThreadExecutorOptions : ExecutorOptions
    {
        public SingleThreadExecutorOptions(string stageName) : base(stageName)
        {
        }
    }

    internal abstract class ExecutorOptions
    {
        protected ExecutorOptions(string stageName)
        {
            StageName = stageName;
        }
        
        public string StageName { get; }
    }
}