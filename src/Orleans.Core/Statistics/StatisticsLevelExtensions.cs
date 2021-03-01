using Orleans.Runtime.Configuration;

namespace Orleans.Runtime
{
    internal static class StatisticsLevelExtensions
    {
        private static bool IsInfo(this StatisticsLevel level) => level >= StatisticsLevel.Info;

        private static bool IsVerbose(this StatisticsLevel level) => level >= StatisticsLevel.Verbose;

        private static bool IsVerbose2(this StatisticsLevel level) => level >= StatisticsLevel.Verbose2;
        
        //---------------------------//

        public static bool CollectPerWorkItemStats(this StatisticsLevel level) => level.IsVerbose2();

        // CollectTurnsStats should be at least as Verbose as CollectPerWorkItemStats
        public static bool CollectTurnsStats(this StatisticsLevel level) => level.IsVerbose2();

        //--------------------------------------//

        public static bool CollectQueueStats(this StatisticsLevel level) => level.IsVerbose2();
        
        public static bool CollectThreadTimeTrackingStats(this StatisticsLevel level) => level.IsVerbose2();

        public static bool ReportDetailedThreadTimeTrackingStats(this StatisticsLevel level) => level.IsVerbose2();

        public static bool PerformStageAnalysis(this StatisticsLevel level) => level.IsVerbose2();

        public static bool CollectShedulerQueuesStats(this StatisticsLevel level) => level.IsVerbose2();

        public static bool CollectApplicationRequestsStats(this StatisticsLevel level) => level.IsInfo();
    }
}


// Scheduler statistics:
// 1) Turn execution stats - collected inside SchedulerStatisticsGroup only if CollectTurnsStats is true
// 2) Turn enqueing/dequeuing stats - collected inside SchedulerStatisticsGroup only if CollectGlobalShedulerStats is true
// 3) WorkItemGroupStatuses  - collected inside SchedulerStatisticsGroup only if CollectPerWorkItemStats is true
// 4) AverageQueueLenght - collected directly at their classes - inside WorkItemGroup, WorkQueue, AsyncQueueAgent, ... only if CollectQueueSizeStats is true
// 5) TimeTrackingStatistic - used by Queue agent for now. Need to be added for worker threads. Only if CollectThreadTimeTrackingStats is true.
// 6) Turn execution time - tracked with TimeInterval, cheap/expensive TimeInterval based on whether MeasureFineGrainedTime is true.
// 7) Queuing delay stats - not collected yet.
