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
    
    internal abstract class ExecutorOptions
    {
        protected ExecutorOptions(
            string stageName,
            Type stageType,
            CancellationToken cancellationToken, 
            ILogger log, 
            ExecutorFaultHandler onFault)
        {
            StageName = stageName;
            StageType = stageType;
            CancellationToken = cancellationToken;
            Log = log;
            OnFault = onFault;
        }

        public string StageName { get; } // rename to Name.

        public Type StageType { get; }

        public string StageTypeName => StageType.Name;  // rename to StageName.

        public CancellationToken CancellationToken { get; }

        public ILogger Log { get; }

        public ExecutorFaultHandler OnFault { get; }
    }

    internal class ThreadPoolExecutorOptions : ExecutorOptions
    {
        public ThreadPoolExecutorOptions(
            string stageName,
            Type stageType,
            CancellationToken ct,
            ILogger log,
            int degreeOfParallelism = 1,
            bool drainAfterCancel = false,
            TimeSpan? workItemExecutionTimeTreshold = null,
            TimeSpan? delayWarningThreshold = null,
            WorkItemStatusProvider workItemStatusProvider = null,
            ExecutorFaultHandler onFault = null)
            : base(stageName, stageType, ct, log, onFault)
        {
            DegreeOfParallelism = degreeOfParallelism;
            DrainAfterCancel = drainAfterCancel;
            WorkItemExecutionTimeTreshold = workItemExecutionTimeTreshold ?? TimeSpan.MaxValue;
            DelayWarningThreshold = delayWarningThreshold ?? TimeSpan.MaxValue;
            WorkItemStatusProvider = workItemStatusProvider;
        }

        public int DegreeOfParallelism { get; }

        public bool DrainAfterCancel { get; }

        public TimeSpan WorkItemExecutionTimeTreshold { get; }

        public TimeSpan DelayWarningThreshold { get; }

        public WorkItemStatusProvider WorkItemStatusProvider { get; }
    }

    internal class SingleThreadExecutorOptions : ExecutorOptions
    {
        public SingleThreadExecutorOptions(
            string stageName,
            Type stageType,
            CancellationToken ct, 
            ILogger log, 
            ExecutorFaultHandler onFault = null) 
            : base(stageName, stageType, ct, log, onFault)
        {
        }
    }

    internal delegate void ExecutorFaultHandler(Exception ex, string executorExplanation);
}