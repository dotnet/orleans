using System;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    internal interface IExecutor : IHealthCheckable
    {
        void QueueWorkItem(WaitCallback callback, object state = null);

        int WorkQueueCount { get; }
    }

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
            string name,
            Type stageType,
            CancellationToken cancellationToken, 
            ILogger log, 
            ExecutorFaultHandler faultHandler)
        {
            Name = name;
            StageType = stageType;
            CancellationToken = cancellationToken;
            Log = log;
            FaultHandler = faultHandler;
        }

        public string Name { get; }

        public Type StageType { get; }

        public string StageTypeName => StageType.Name; 

        public CancellationToken CancellationToken { get; }

        public ILogger Log { get; }

        public ExecutorFaultHandler FaultHandler { get; }
    }

    internal class ThreadPoolExecutorOptions : ExecutorOptions
    {
        public ThreadPoolExecutorOptions(
            string name,
            Type stageType,
            CancellationToken ct,
            ILogger log,
            int degreeOfParallelism = 1,
            bool drainAfterCancel = false,
            TimeSpan? workItemExecutionTimeTreshold = null,
            TimeSpan? delayWarningThreshold = null,
            WorkItemStatusProvider workItemStatusProvider = null,
            ExecutorFaultHandler faultHandler = null)
            : base(name, stageType, ct, log, faultHandler)
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
            string name,
            Type stageType,
            CancellationToken ct, 
            ILogger log, 
            ExecutorFaultHandler faultHandler = null) 
            : base(name, stageType, ct, log, faultHandler)
        {
        }
    }

    internal delegate void ExecutorFaultHandler(Exception ex, string executorExplanation);
}