using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Counters;

namespace Orleans.Runtime.Scheduler
{
    internal class OrleansSchedulerAsynchAgent : AsynchQueueAgent<IWorkItem>
    {
        private readonly QueueTrackingStatistic queueTracking;
        private readonly ThreadPoolExecutorOptions executorOptions;
        private readonly TaskScheduler taskScheduler;

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
            executorOptions = new ThreadPoolExecutorOptions(
                Name,
                GetType(),
                Cts.Token,
                Log,
                maxDegreeOfParalelism,
                drainAfterCancel,
                false,
                turnWarningLengthThreshold,
                delayWarningThreshold,
                GetWorkItemStatus,
                ExecutorFaultHandler);
            taskScheduler = scheduler;

            if (!StatisticsCollector.CollectShedulerQueuesStats) return;
            queueTracking = new QueueTrackingStatistic(queueTrackingName);
            queueTracking.OnStartExecution();
        }

        protected override void Process(IWorkItem request)
        {
            RuntimeContext.InitializeThread(taskScheduler);

            TrackWorkItemDequeue();
            try
            {
                RuntimeContext.SetExecutionContext(request.SchedulingContext, taskScheduler);
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

        protected override ExecutorOptions ExecutorOptions => executorOptions;

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
