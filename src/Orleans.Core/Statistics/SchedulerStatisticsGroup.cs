using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime
{
    internal class SchedulerStatisticsGroup
    {
        private CounterStatistic[] turnsExecutedPerWorkerThreadApplicationTurns;
        private CounterStatistic[] turnsExecutedPerWorkerThreadSystemTurns;
        private CounterStatistic[] turnsExecutedPerWorkerThreadNull;
        private readonly CounterStatistic turnsExecutedByAllWorkerThreadsTotalApplicationTurns;
        private readonly CounterStatistic turnsExecutedByAllWorkerThreadsTotalSystemTurns;
        private readonly CounterStatistic turnsExecutedByAllWorkerThreadsTotalNullTurns;

        private CounterStatistic[] turnsExecutedPerWorkItemGroup;
        private StringValueStatistic[] workItemGroupStatuses;
        private readonly CounterStatistic turnsExecutedByAllWorkItemGroupsTotalApplicationTurns;
        private readonly CounterStatistic turnsExecutedByAllWorkItemGroupsTotalSystem;
        private readonly CounterStatistic totalPendingWorkItems;
        private readonly CounterStatistic turnsExecutedStartTotal;
        private readonly CounterStatistic turnsExecutedEndTotal;

        private readonly CounterStatistic turnsEnQueuedTotal;
        private readonly CounterStatistic turnsDeQueuedTotal;
        private readonly CounterStatistic turnsDroppedTotal;
        private readonly CounterStatistic closureWorkItemsCreated;
        private readonly CounterStatistic closureWorkItemsExecuted;

        private readonly HistogramValueStatistic turnLengthHistogram;
        private const int TURN_LENGTH_HISTOGRAM_SIZE = 31;

        private int workerThreadCounter;
        private int workItemGroupCounter;
        private readonly object lockable;
        private readonly ILogger logger;
        private readonly StatisticsLevel collectionLevel;

        public SchedulerStatisticsGroup(IOptions<StatisticsOptions> statisticsOptions, ILogger<SchedulerStatisticsGroup> logger)
        {
            this.logger = logger;
            this.collectionLevel = statisticsOptions.Value.CollectionLevel;
            this.CollectGlobalShedulerStats = collectionLevel.CollectGlobalShedulerStats();
            this.CollectTurnsStats = collectionLevel.CollectTurnsStats();
            this.CollectPerWorkItemStats = collectionLevel.CollectPerWorkItemStats();
            this.CollectShedulerQueuesStats = collectionLevel.CollectShedulerQueuesStats();

            workItemGroupStatuses = new StringValueStatistic[1];
            workerThreadCounter = 0;
            workItemGroupCounter = 0;
            lockable = new object();

            if (this.CollectGlobalShedulerStats)
            {
                totalPendingWorkItems = CounterStatistic.FindOrCreate(StatisticNames.SCHEDULER_PENDINGWORKITEMS, false);
                turnsEnQueuedTotal = CounterStatistic.FindOrCreate(StatisticNames.SCHEDULER_ITEMS_ENQUEUED_TOTAL);
                turnsDeQueuedTotal = CounterStatistic.FindOrCreate(StatisticNames.SCHEDULER_ITEMS_DEQUEUED_TOTAL);
                turnsDroppedTotal = CounterStatistic.FindOrCreate(StatisticNames.SCHEDULER_ITEMS_DROPPED_TOTAL);
                closureWorkItemsCreated = CounterStatistic.FindOrCreate(StatisticNames.SCHEDULER_CLOSURE_WORK_ITEMS_CREATED);
                closureWorkItemsExecuted = CounterStatistic.FindOrCreate(StatisticNames.SCHEDULER_CLOSURE_WORK_ITEMS_EXECUTED);
            }

            if (this.CollectTurnsStats)
            {
                turnsExecutedByAllWorkerThreadsTotalApplicationTurns = CounterStatistic.FindOrCreate(StatisticNames.SCHEDULER_TURNSEXECUTED_APPLICATION_BYALLWORKERTHREADS);
                turnsExecutedByAllWorkerThreadsTotalSystemTurns = CounterStatistic.FindOrCreate(StatisticNames.SCHEDULER_TURNSEXECUTED_SYSTEM_BYALLWORKERTHREADS);
                turnsExecutedByAllWorkerThreadsTotalNullTurns = CounterStatistic.FindOrCreate(StatisticNames.SCHEDULER_TURNSEXECUTED_NULL_BYALLWORKERTHREADS);

                turnsExecutedByAllWorkItemGroupsTotalApplicationTurns = CounterStatistic.FindOrCreate(StatisticNames.SCHEDULER_TURNSEXECUTED_APPLICATION_BYALLWORKITEMGROUPS);
                turnsExecutedByAllWorkItemGroupsTotalSystem = CounterStatistic.FindOrCreate(StatisticNames.SCHEDULER_TURNSEXECUTED_SYSTEM_BYALLWORKITEMGROUPS);
                turnLengthHistogram = ExponentialHistogramValueStatistic.Create_ExponentialHistogram_ForTiming(StatisticNames.SCHEDULER_TURN_LENGTH_HISTOGRAM, TURN_LENGTH_HISTOGRAM_SIZE);
                turnsExecutedStartTotal = CounterStatistic.FindOrCreate(StatisticNames.SCHEDULER_TURNSEXECUTED_TOTAL_START);
                turnsExecutedEndTotal = CounterStatistic.FindOrCreate(StatisticNames.SCHEDULER_TURNSEXECUTED_TOTAL_END);

                turnsExecutedPerWorkerThreadApplicationTurns = new CounterStatistic[1];
                turnsExecutedPerWorkerThreadSystemTurns = new CounterStatistic[1];
                turnsExecutedPerWorkerThreadNull = new CounterStatistic[1];
                turnsExecutedPerWorkItemGroup = new CounterStatistic[1];
            }

            NumLongRunningTurns = CounterStatistic.FindOrCreate(StatisticNames.SCHEDULER_NUM_LONG_RUNNING_TURNS);
            NumLongQueueWaitTimes = CounterStatistic.FindOrCreate(StatisticNames.SCHEDULER_NUM_LONG_QUEUE_WAIT_TIMES);
        }

        public bool CollectShedulerQueuesStats { get; }

        public bool CollectPerWorkItemStats { get; }

        public bool CollectTurnsStats { get; }

        public bool CollectGlobalShedulerStats { get; }

        public CounterStatistic NumLongRunningTurns { get; }

        public CounterStatistic NumLongQueueWaitTimes { get; }

        internal int RegisterWorkingThread(string threadName)
        {
            lock (lockable)
            {
                int i = workerThreadCounter;
                workerThreadCounter++;
                if (i == turnsExecutedPerWorkerThreadApplicationTurns.Length)
                {
                    // need to resize the array
                    Array.Resize(ref turnsExecutedPerWorkerThreadApplicationTurns, 2 * turnsExecutedPerWorkerThreadApplicationTurns.Length);
                    Array.Resize(ref turnsExecutedPerWorkerThreadSystemTurns, 2 * turnsExecutedPerWorkerThreadSystemTurns.Length);
                    Array.Resize(ref turnsExecutedPerWorkerThreadNull, 2 * turnsExecutedPerWorkerThreadNull.Length);
                }
                turnsExecutedPerWorkerThreadApplicationTurns[i] = CounterStatistic.FindOrCreate(new StatisticName(StatisticNames.SCHEDULER_TURNSEXECUTED_APPLICATION_PERTHREAD, threadName));
                turnsExecutedPerWorkerThreadSystemTurns[i] = CounterStatistic.FindOrCreate(new StatisticName(StatisticNames.SCHEDULER_TURNSEXECUTED_SYSTEM_PERTHREAD, threadName));
                turnsExecutedPerWorkerThreadNull[i] = CounterStatistic.FindOrCreate(new StatisticName(StatisticNames.SCHEDULER_TURNSEXECUTED_NULL_PERTHREAD, threadName));
                return i;
            }
        }

        internal int RegisterWorkItemGroup(string workItemGroupName, ISchedulingContext context, Func<string> statusGetter)
        {
            lock (lockable)
            {
                int i = workItemGroupCounter;
                workItemGroupCounter++;
                if (i == turnsExecutedPerWorkItemGroup.Length)
                {
                    // need to resize the array
                    Array.Resize(ref turnsExecutedPerWorkItemGroup, 2 * turnsExecutedPerWorkItemGroup.Length);
                    Array.Resize(ref workItemGroupStatuses, 2 * workItemGroupStatuses.Length);
                }
                CounterStorage storage =  ReportPerWorkItemStats(context) ? CounterStorage.LogAndTable : CounterStorage.DontStore;
                turnsExecutedPerWorkItemGroup[i] = CounterStatistic.FindOrCreate(new StatisticName(StatisticNames.SCHEDULER_ACTIVATION_TURNSEXECUTED_PERACTIVATION, workItemGroupName), storage);
                workItemGroupStatuses[i] = StringValueStatistic.FindOrCreate(new StatisticName(StatisticNames.SCHEDULER_ACTIVATION_STATUS_PERACTIVATION, workItemGroupName), statusGetter, storage);
                return i;
            }

            bool ReportPerWorkItemStats(ISchedulingContext schedulingContext)
            {
                return SchedulingUtils.IsSystemPriorityContext(schedulingContext)
                    ? this.collectionLevel >= StatisticsLevel.Verbose2
                    : this.collectionLevel >= StatisticsLevel.Verbose3;
            }
        }

        internal void UnRegisterWorkItemGroup(int workItemGroup)
        {
            Utils.SafeExecute(() => CounterStatistic.Delete(turnsExecutedPerWorkItemGroup[workItemGroup].Name),
                logger,
                () => String.Format("SchedulerStatisticsGroup.UnRegisterWorkItemGroup({0})", turnsExecutedPerWorkItemGroup[workItemGroup].Name));

            Utils.SafeExecute(() => StringValueStatistic.Delete(workItemGroupStatuses[workItemGroup].Name),
                logger,
                () => String.Format("SchedulerStatisticsGroup.UnRegisterWorkItemGroup({0})", workItemGroupStatuses[workItemGroup].Name));  
        }

        //----------- Global scheduler stats ---------------------//
        internal void OnWorkItemEnqueue()
        {
            totalPendingWorkItems.Increment();
            turnsEnQueuedTotal.Increment();
        }

        internal void OnWorkItemDequeue()
        {
            totalPendingWorkItems.DecrementBy(1);
            turnsDeQueuedTotal.Increment(); 
        }

        internal void OnWorkItemDrop(int n)
        {
            totalPendingWorkItems.DecrementBy(n);
            turnsDroppedTotal.IncrementBy(n);
        }

        internal void OnClosureWorkItemsCreated()
        {
            closureWorkItemsCreated.Increment();
        }

        internal void OnClosureWorkItemsExecuted()
        {
            closureWorkItemsExecuted.Increment();
        }

        //------

        internal void OnThreadStartsTurnExecution(int workerThread, ISchedulingContext context)
        {
            turnsExecutedStartTotal.Increment();
            if (context == null)
            {
                turnsExecutedPerWorkerThreadNull[workerThread].Increment();
                turnsExecutedByAllWorkerThreadsTotalNullTurns.Increment();
            }
            else if (context.ContextType == SchedulingContextType.SystemTarget)
            {
                turnsExecutedPerWorkerThreadSystemTurns[workerThread].Increment();
                turnsExecutedByAllWorkerThreadsTotalSystemTurns.Increment();
            }
            else if (context.ContextType == SchedulingContextType.Activation)
            {
                turnsExecutedPerWorkerThreadApplicationTurns[workerThread].Increment();
                turnsExecutedByAllWorkerThreadsTotalApplicationTurns.Increment();
            }
        }

        internal void OnTurnExecutionStartsByWorkGroup(int workItemGroup, int workerThread, ISchedulingContext context)
        {
            turnsExecutedStartTotal.Increment();
            turnsExecutedPerWorkItemGroup[workItemGroup].Increment();
            

            if (context == null)
            {
                throw new ArgumentException(String.Format("Cannot execute null context work item on work item group {0}.", workItemGroup));
            }

            if (context.ContextType == SchedulingContextType.SystemTarget)
            {
                turnsExecutedByAllWorkItemGroupsTotalSystem.Increment();
                turnsExecutedPerWorkerThreadSystemTurns[workerThread].Increment();
                turnsExecutedByAllWorkerThreadsTotalSystemTurns.Increment();
            }
            else if (context.ContextType == SchedulingContextType.Activation)
            {
                turnsExecutedByAllWorkItemGroupsTotalApplicationTurns.Increment();
                turnsExecutedPerWorkerThreadApplicationTurns[workerThread].Increment();
                turnsExecutedByAllWorkerThreadsTotalApplicationTurns.Increment();
            }
        }

        internal void OnTurnExecutionEnd(TimeSpan timeSpan)
        {
            turnLengthHistogram.AddData(timeSpan);
            turnsExecutedEndTotal.Increment();
        }
    }
}

