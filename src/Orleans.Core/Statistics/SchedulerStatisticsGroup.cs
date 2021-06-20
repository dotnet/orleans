using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime
{
    internal class SchedulerStatisticsGroup
    {
        private CounterStatistic[] turnsExecutedPerWorkItemGroup;
        private StringValueStatistic[] workItemGroupStatuses;

        private int workItemGroupCounter;
        private readonly object lockable;
        private readonly ILogger logger;
        private readonly StatisticsLevel collectionLevel;

        public SchedulerStatisticsGroup(IOptions<StatisticsOptions> statisticsOptions, ILogger<SchedulerStatisticsGroup> logger)
        {
            this.logger = logger;
            this.collectionLevel = statisticsOptions.Value.CollectionLevel;
            this.CollectTurnsStats = collectionLevel.CollectTurnsStats();
            this.CollectPerWorkItemStats = collectionLevel.CollectPerWorkItemStats();
            this.CollectShedulerQueuesStats = collectionLevel.CollectShedulerQueuesStats();

            workItemGroupStatuses = new StringValueStatistic[1];
            workItemGroupCounter = 0;
            lockable = new object();

            if (this.CollectTurnsStats)
            {
                turnsExecutedPerWorkItemGroup ??= new CounterStatistic[1];
            }

            NumLongRunningTurns = CounterStatistic.FindOrCreate(StatisticNames.SCHEDULER_NUM_LONG_RUNNING_TURNS);
            NumLongQueueWaitTimes = CounterStatistic.FindOrCreate(StatisticNames.SCHEDULER_NUM_LONG_QUEUE_WAIT_TIMES);
        }

        public bool CollectShedulerQueuesStats { get; }

        public bool CollectPerWorkItemStats { get; }

        public bool CollectTurnsStats { get; }

        public CounterStatistic NumLongRunningTurns { get; }

        public CounterStatistic NumLongQueueWaitTimes { get; }

        internal int RegisterWorkItemGroup(string workItemGroupName, IGrainContext context, Func<string> statusGetter)
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

            bool ReportPerWorkItemStats(IGrainContext context)
            {
                return context is ISystemTargetBase
                    ? this.collectionLevel >= StatisticsLevel.Verbose2
                    : this.collectionLevel >= StatisticsLevel.Verbose3;
            }
        }

        internal void UnregisterWorkItemGroup(int workItemGroup)
        {
            Utils.SafeExecute(() => CounterStatistic.Delete(turnsExecutedPerWorkItemGroup[workItemGroup].Name),
                logger,
                () => String.Format("SchedulerStatisticsGroup.UnRegisterWorkItemGroup({0})", turnsExecutedPerWorkItemGroup[workItemGroup].Name));

            Utils.SafeExecute(() => StringValueStatistic.Delete(workItemGroupStatuses[workItemGroup].Name),
                logger,
                () => String.Format("SchedulerStatisticsGroup.UnRegisterWorkItemGroup({0})", workItemGroupStatuses[workItemGroup].Name));  
        }
    }
}

