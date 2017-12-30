using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.Threading
{
    internal interface IExecutor : IHealthCheckable // todo: move IHealthCheckable to ThreadPoolExecutor
    {
        /// <summary>
        /// Executes the given command at some time in the future.  The command
        /// may execute in a new thread, in a pooled thread, or in the calling thread
        /// </summary>
        void QueueWorkItem(WaitCallback callback, object state = null);
    }

    internal abstract class ExecutorOptions
    {
        protected ExecutorOptions(
            string name,
            Type stageType,
            CancellationToken cancellationToken,
            ILoggerFactory loggerFactory,
            ExecutorFaultHandler faultHandler = null)
        {
            Name = name;
            StageType = stageType;
            CancellationToken = cancellationToken;
            LoggerFactory = loggerFactory;
            FaultHandler = faultHandler;
        }

        public string Name { get; }

        public Type StageType { get; }

        public string StageTypeName => StageType.Name; 

        public CancellationToken CancellationToken { get; }

        public ILoggerFactory LoggerFactory { get; }
        
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
            ILoggerFactory loggerFactory,
            int degreeOfParallelism = 1,
            bool drainAfterCancel = false,
            bool preserveOrder = true,
            TimeSpan? workItemExecutionTimeTreshold = null,
            TimeSpan? delayWarningThreshold = null,
            WorkItemStatusProvider workItemStatusProvider = null,
            ExecutorFaultHandler faultHandler = null)
            : base(name, stageType, ct, loggerFactory, faultHandler)
        {
            DegreeOfParallelism = degreeOfParallelism;
            DrainAfterCancel = drainAfterCancel;
            PreserveOrder = preserveOrder;
            WorkItemExecutionTimeTreshold = workItemExecutionTimeTreshold ?? TimeSpan.MaxValue;
            DelayWarningThreshold = delayWarningThreshold ?? TimeSpan.MaxValue;
            WorkItemStatusProvider = workItemStatusProvider;
        }

        public int DegreeOfParallelism { get; private set; }

        public bool DrainAfterCancel { get; private set; }

        public bool PreserveOrder { get; private set; }

        public TimeSpan WorkItemExecutionTimeTreshold { get; private set; }

        public TimeSpan DelayWarningThreshold { get; private set; }

        public WorkItemStatusProvider WorkItemStatusProvider { get; private set; }
        
        public class Builder
        {
            public Builder(
                string name,
                Type stageType,
                CancellationToken cancellationToken,
                ILoggerFactory loggerFactory,
                ExecutorFaultHandler faultHandler = null)
            {
                Options = new ThreadPoolExecutorOptions(name, stageType, cancellationToken, loggerFactory, faultHandler: faultHandler);
            }

            public ThreadPoolExecutorOptions Options { get; }

            public Builder WithDegreeOfParallelism(int degreeOfParallelism)
            {
                Options.DegreeOfParallelism = degreeOfParallelism;
                return this;
            }

            public Builder WithDrainAfterCancel(bool drainAfterCancel)
            {
                Options.DrainAfterCancel = drainAfterCancel;
                return this;
            }

            public Builder WithPreserveOrder(bool preserveOrder)
            {
                Options.PreserveOrder = preserveOrder;
                return this;
            }

            public Builder WithWorkItemExecutionTimeTreshold(
                TimeSpan workItemExecutionTimeTreshold)
            {
                Options.WorkItemExecutionTimeTreshold = workItemExecutionTimeTreshold;
                return this;
            }

            public Builder WithDelayWarningThreshold(TimeSpan delayWarningThreshold)
            {
                Options.DelayWarningThreshold = delayWarningThreshold;
                return this;
            }

            public Builder WithWorkItemStatusProvider(
                WorkItemStatusProvider workItemStatusProvider)
            {
                Options.WorkItemStatusProvider = workItemStatusProvider;
                return this;
            }
        }

        public delegate Builder BuilderConfigurator(Builder builder);
    }

    internal delegate void ExecutorFaultHandler(Exception ex, string executorExplanation);
}