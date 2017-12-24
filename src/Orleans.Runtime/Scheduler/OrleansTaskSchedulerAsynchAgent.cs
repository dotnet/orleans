using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Threading;

namespace Orleans.Runtime.Scheduler
{
    internal class OrleansSchedulerAsynchAgent : AsynchQueueAgent<IWorkItem>
    {
        private readonly QueueTrackingStatistic queueTracking;
        private readonly TaskScheduler scheduler;

        public OrleansSchedulerAsynchAgent(
            string name,
            string queueTrackingName,
            ExecutorService executorService,
            int maxDegreeOfParalelism, 
            TimeSpan delayWarningThreshold, 
            TimeSpan turnWarningLengthThreshold,
            TaskScheduler scheduler,
            bool drainAfterCancel,
            ILoggerFactory loggerFactory) : base(name, executorService, loggerFactory)
        {
            // todo: + executor options builder;
            // + queue configuration? 
            ExecutorOptions = new ThreadPoolExecutorOptions(
                Name,
                GetType(),
                Cts.Token,
                loggerFactory,
                maxDegreeOfParalelism,
                drainAfterCancel,
                false,
                turnWarningLengthThreshold,
                delayWarningThreshold,
                GetWorkItemStatus,
                ExecutorFaultHandler);

            this.scheduler = scheduler;

            if (!StatisticsCollector.CollectShedulerQueuesStats) return;
            queueTracking = new QueueTrackingStatistic(queueTrackingName);
            queueTracking.OnStartExecution();
        }

        protected override void Process(IWorkItem request)
        {
            RuntimeContext.InitializeThread(scheduler);

            TrackWorkItemDequeue();
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

        public override void Stop()
        {
            base.Stop();
            if (!StatisticsCollector.CollectShedulerQueuesStats) return;
            queueTracking.OnStopExecution();
        }

        protected override ThreadPoolExecutorOptions ExecutorOptions { get; }

        private string GetWorkItemStatus(object item, bool detailed)
        {
            if (!detailed || !(item is IWorkItem workItem)) return string.Empty;
            return workItem is WorkItemGroup group ? string.Format("WorkItemGroup Details: {0}", group.DumpStatus()) : string.Empty;
        }

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
