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
    internal class OrleansSchedulerSystemAgent : OrleansSchedulerAsynchAgent
    {
        public OrleansSchedulerSystemAgent(
            string nameSuffix,
            ExecutorService executorService, 
            int maxDegreeOfParalelism,
            TimeSpan delayWarningThreshold,
            TimeSpan turnWarningLengthThreshold,
            TaskScheduler scheduler,
            ILoggerFactory loggerFactory) : base(
                nameSuffix,
                executorService,
                maxDegreeOfParalelism,
                delayWarningThreshold,
                turnWarningLengthThreshold, 
                scheduler,
                loggerFactory)
        {
        }
        protected override bool DrainAfterCancel => true;
    }

    internal class OrleansSchedulerMainAgent : OrleansSchedulerAsynchAgent
    {
        public OrleansSchedulerMainAgent(
            string nameSuffix,
            ExecutorService executorService,
            int maxDegreeOfParalelism,
            TimeSpan delayWarningThreshold, 
            TimeSpan turnWarningLengthThreshold,
            TaskScheduler scheduler,
            ILoggerFactory loggerFactory) :
            base(
                nameSuffix,
                executorService,
                maxDegreeOfParalelism,
                delayWarningThreshold,
                turnWarningLengthThreshold,
                scheduler,
                loggerFactory)
        {
        }
    }

    internal abstract class OrleansSchedulerAsynchAgent : AsynchQueueAgent<IWorkItem>
    {
   //     private readonly QueueTrackingStatistic queueTracking; //todo
        private readonly ThreadPoolExecutorOptions executorOptions;

        private readonly TaskScheduler taskScheduler;

        public OrleansSchedulerAsynchAgent(
            string name,
            ExecutorService executorService,
            int maxDegreeOfParalelism, 
            TimeSpan delayWarningThreshold, 
            TimeSpan turnWarningLengthThreshold,
            TaskScheduler scheduler,
            ILoggerFactory loggerFactory) : base(name, executorService, loggerFactory)
        {
            executorOptions = new ThreadPoolExecutorOptions(
                Name,
                GetType(),
                Cts.Token,
                Log,
                maxDegreeOfParalelism,
                DrainAfterCancel, // todo: virtual call
                turnWarningLengthThreshold,
                delayWarningThreshold,
                GetWorkItemStatus,
                ExecutorFaultHandler);
            taskScheduler = scheduler;
            //mainQueueTracking = new QueueTrackingStatistic("Scheduler.LevelOne.MainQueue"); todo:
            //systemQueueTracking = new QueueTrackingStatistic("Scheduler.LevelOne.SystemQueue");

            //mainQueueTracking.OnStartExecution();
            //systemQueueTracking.OnStartExecution();
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
            if (!StatisticsCollector.CollectShedulerQueuesStats) return;
           // queueTracking.OnStopExecution();
        }

        protected override ExecutorOptions GetExecutorOptions()
        {
            return new ThreadPoolExecutorOptions(
                Name,
                GetType(), 
                Cts.Token, 
                Log, 
                drainAfterCancel: DrainAfterCancel, 
                faultHandler: ExecutorFaultHandler);
        }

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
