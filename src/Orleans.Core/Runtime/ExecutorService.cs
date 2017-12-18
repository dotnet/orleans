using System;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    internal interface IExecutor : IHealthCheckable
    {
        /// <summary>
        /// Executes the given command at some time in the future.  The command
        /// may execute in a new thread, in a pooled thread, or in the calling thread
        /// </summary>
        void QueueWorkItem(WaitCallback callback, object state = null);
    }

    internal class ExecutorService
    {
        public TExecutor GetExecutor<TExecutor>(ExecutorOptions executorOptions) where TExecutor : IExecutor
        {
            switch (executorOptions)
            {
                case ThreadPoolExecutorOptions options:
                    return (TExecutor)(object)new ThreadPoolExecutor(options); // todo: remove (TExecutor)(object)
                case SingleThreadExecutorOptions options:
                    return (TExecutor)(object)new ThreadPerTaskExecutor(options);
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
            ExecutorFaultHandler faultHandler = null)
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


        public const bool TRACK_DETAILED_STATS = false;

        // todo: consider making StatisticsCollector.CollectThreadTimeTrackingStats static
        public static bool CollectDetailedThreadStatistics =
            TRACK_DETAILED_STATS && StatisticsCollector.CollectThreadTimeTrackingStats;

        public static bool CollectDetailedQueueStatistics =
            TRACK_DETAILED_STATS && StatisticsCollector.CollectQueueStats;
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
            bool preserveOrder = true,
            TimeSpan? workItemExecutionTimeTreshold = null,
            TimeSpan? delayWarningThreshold = null,
            WorkItemStatusProvider workItemStatusProvider = null,
            ExecutorFaultHandler faultHandler = null)
            : base(name, stageType, ct, log, faultHandler)
        {
            DegreeOfParallelism = degreeOfParallelism;
            DrainAfterCancel = drainAfterCancel;
            PreserveOrder = preserveOrder;
            WorkItemExecutionTimeTreshold = workItemExecutionTimeTreshold ?? TimeSpan.MaxValue;
            DelayWarningThreshold = delayWarningThreshold ?? TimeSpan.MaxValue;
            WorkItemStatusProvider = workItemStatusProvider;
        }

        public int DegreeOfParallelism { get; }

        public bool DrainAfterCancel { get; }

        public bool PreserveOrder { get; }

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