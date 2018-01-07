using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Threading;

namespace Orleans.Runtime.Scheduler
{
    internal class OrleansSchedulerAsynchAgent : AsynchQueueAgent<IWorkItem>
    {
        private readonly TaskScheduler scheduler;

        private readonly ThreadPoolExecutorOptions.BuilderConfigurator configureExecutorOptionsBuilder;

        public OrleansSchedulerAsynchAgent(
            string name,
            ExecutorService executorService,
            int maxDegreeOfParalelism, 
            TimeSpan delayWarningThreshold, 
            TimeSpan turnWarningLengthThreshold,
            TaskScheduler scheduler,
            bool drainAfterCancel,
            ILoggerFactory loggerFactory) : base(name, executorService, loggerFactory)
        {
            this.scheduler = scheduler;

            configureExecutorOptionsBuilder = builder => builder
                .WithDegreeOfParallelism(maxDegreeOfParalelism)
                .WithDrainAfterCancel(drainAfterCancel)
                .WithPreserveOrder(false)
                .WithWorkItemExecutionTimeTreshold(turnWarningLengthThreshold)
                .WithDelayWarningThreshold(delayWarningThreshold)
                .WithWorkItemStatusProvider(GetWorkItemStatus)
                .WithExecutionFilters(new SchedulerStatisticsTracker(this));
        }

        protected override void Process(IWorkItem request)
        {
            RuntimeContext.InitializeThread(scheduler);
            try
            {
                RuntimeContext.SetExecutionContext(request.SchedulingContext, scheduler);
                request.Execute();
            }
            finally
            {
                RuntimeContext.ResetExecutionContext();
            }
        }

        protected override void OnEnqueue(IWorkItem request)
        {
            base.OnEnqueue(request);

#if TRACK_DETAILED_STATS
            if (StatisticsCollector.CollectGlobalShedulerStats)
                SchedulerStatisticsGroup.OnWorkItemEnqueue();
#endif
        }

        protected override ThreadPoolExecutorOptions.Builder ExecutorOptionsBuilder => configureExecutorOptionsBuilder(base.ExecutorOptionsBuilder);

        private string GetWorkItemStatus(object item, bool detailed)
        {
            if (!detailed || !(item is IWorkItem workItem)) return string.Empty;
            return workItem is WorkItemGroup group ? string.Format("WorkItemGroup Details: {0}", group.DumpStatus()) : string.Empty;
        }

        private sealed class SchedulerStatisticsTracker : ExecutionFilter
        {
            private readonly OrleansSchedulerAsynchAgent agent;

            public SchedulerStatisticsTracker(OrleansSchedulerAsynchAgent agent)
            {
                this.agent = agent;
            }

            public override Action<ExecutionContext> OnActionExecuting => context => TrackWorkItemDequeue();

            public override Action<ExecutionContext> OnActionExecuted => context =>
            {
#if TRACK_DETAILED_STATS
                if (StatisticsCollector.CollectTurnsStats)
                {
                    if (agent.GetWorkItemState(context).ItemType != WorkItemType.WorkItemGroup)
                    {
                        SchedulerStatisticsGroup.OnTurnExecutionEnd(Utils.Since(context.WorkItem.ExecutionStart));
                    }
                }
#endif
            };

            private void TrackWorkItemDequeue()
            {
#if TRACK_DETAILED_STATS
                if (StatisticsCollector.CollectGlobalShedulerStats)
                {
                    SchedulerStatisticsGroup.OnWorkItemDequeue();
                }
#endif
            }
        }
    }
}
