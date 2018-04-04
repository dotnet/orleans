using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.Threading
{
    internal abstract class ExecutorOptions
    {
        protected ExecutorOptions(
            string name,
            Type stageType,
            CancellationTokenSource cancellationTokenSource,
            ILoggerFactory loggerFactory)
        {
            Name = name;
            StageType = stageType;
            CancellationTokenSource = cancellationTokenSource;
            LoggerFactory = loggerFactory;
        }

        public string Name { get; }

        public Type StageType { get; }

        public string StageTypeName => StageType.Name; 

        public CancellationTokenSource CancellationTokenSource { get; }

        public ILoggerFactory LoggerFactory { get; }

        public static readonly bool TRACK_DETAILED_STATS = false;

        public static bool CollectDetailedThreadStatistics = TRACK_DETAILED_STATS && StatisticsCollector.CollectThreadTimeTrackingStats;

        public static bool CollectDetailedQueueStatistics = TRACK_DETAILED_STATS && StatisticsCollector.CollectQueueStats;
    }


    internal class ThreadPoolExecutorOptions : ExecutorOptions
    {
        public ThreadPoolExecutorOptions(
            string name,
            Type stageType,
            CancellationTokenSource cts,
            ILoggerFactory loggerFactory,
            int degreeOfParallelism = 1,
            bool drainAfterCancel = false,
            bool preserveOrder = true,
            TimeSpan? workItemExecutionTimeTreshold = null,
            TimeSpan? delayWarningThreshold = null,
            WorkItem.StatusProvider workItemStatusProvider = null,
            IEnumerable<ExecutionActionFilter> actionFilters = null,
            IEnumerable<ExecutionExceptionFilter> exceptionFilters = null)
            : base(name, stageType, cts, loggerFactory)
        {
            DegreeOfParallelism = degreeOfParallelism;
            DrainAfterCancel = drainAfterCancel;
            PreserveOrder = preserveOrder;
            WorkItemStatusProvider = workItemStatusProvider;
            WorkItemExecutionTimeTreshold = workItemExecutionTimeTreshold ?? TimeSpan.MaxValue;
            DelayWarningThreshold = delayWarningThreshold ?? TimeSpan.MaxValue;
            ExecutionFilters = actionFilters?.ToArray() ?? Array.Empty<ExecutionActionFilter>();
            ExceptionFilters = exceptionFilters?.ToArray() ?? Array.Empty<ExecutionExceptionFilter>();
        }

        public IReadOnlyCollection<ExecutionActionFilter> ExecutionFilters { get; private set; }

        public IReadOnlyCollection<ExecutionExceptionFilter> ExceptionFilters { get; private set; }

        public int DegreeOfParallelism { get; private set; }

        public bool DrainAfterCancel { get; private set; }

        public bool PreserveOrder { get; private set; }

        public TimeSpan WorkItemExecutionTimeTreshold { get; private set; }

        public TimeSpan DelayWarningThreshold { get; private set; }

        public WorkItem.StatusProvider WorkItemStatusProvider { get; private set; }

        public class Builder
        {
            public Builder(
                string name,
                Type stageType,
                CancellationTokenSource cts,
                ILoggerFactory loggerFactory)
            {
                Options = new ThreadPoolExecutorOptions(name, stageType, cts, loggerFactory);
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

            public Builder WithWorkItemExecutionTimeTreshold(TimeSpan workItemExecutionTimeTreshold)
            {
                Options.WorkItemExecutionTimeTreshold = workItemExecutionTimeTreshold;
                return this;
            }

            public Builder WithDelayWarningThreshold(TimeSpan delayWarningThreshold)
            {
                Options.DelayWarningThreshold = delayWarningThreshold;
                return this;
            }

            public Builder WithActionFilters(params ExecutionActionFilter[] filters)
            {
                Options.ExecutionFilters = Options.ExecutionFilters.Union(filters).ToArray();
                return this;
            }

            public Builder WithExceptionFilters(params ExecutionExceptionFilter[] filters)
            {
                Options.ExceptionFilters = Options.ExceptionFilters.Union(filters).ToArray();
                return this;
            }

            public Builder WithWorkItemStatusProvider(WorkItem.StatusProvider workItemStatusProvider)
            {
                Options.WorkItemStatusProvider = workItemStatusProvider;
                return this;
            }
        }

        public delegate Builder BuilderConfigurator(Builder builder);
    }

    internal delegate bool ExecutorFaultHandler(Exception ex, ExecutionContext context);
}