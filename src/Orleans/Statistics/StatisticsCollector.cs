/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using Orleans.Runtime.Configuration;

namespace Orleans.Runtime
{
    internal static class StatisticsCollector
    {
        public static StatisticsLevel StatisticsCollectionLevel;

        public static void Initialize(IStatisticsConfiguration config)
        {
            StatisticsCollectionLevel = config.StatisticsCollectionLevel;
        }

        private static bool IsInfo
        {
            get { return StatisticsCollectionLevel >= StatisticsLevel.Info; }
        }

        private static bool IsVerbose
        {
            get { return StatisticsCollectionLevel >= StatisticsLevel.Verbose; }
        }

        private static bool IsVerbose2
        {
            get { return StatisticsCollectionLevel >= StatisticsLevel.Verbose2; }
        }

        private static bool IsVerbose3
        {
            get { return StatisticsCollectionLevel >= StatisticsLevel.Verbose3; }
        }

        //---------------------------//

        public static bool CollectGlobalShedulerStats
        {
            get { return IsVerbose; }
        }

        public static bool CollectPerWorkItemStats
        {
            get { return IsVerbose2; }
        }

        public static bool CollectTurnsStats // CollectTurnsStats should be at least as Verbose as CollectPerWorkItemStats
        {
            get { return IsVerbose2; }
        }

        internal static bool ReportPerWorkItemStats(ISchedulingContext schedulingContext)
        {
            return SchedulingUtils.IsSystemContext(schedulingContext) ? IsVerbose2 : IsVerbose3;
        }

        //--------------------------------------//

        public static bool CollectQueueStats
        {
            get { return IsVerbose2; }
        }

        public static bool CollectThreadTimeTrackingStats
        {
            get { return IsVerbose2; }
        }

        public static bool ReportDetailedThreadTimeTrackingStats
        {
            get { return IsVerbose2; }
        }

        public static bool CollectContextSwitchesStats
        {
            get { return IsVerbose2; }
        }

        public static bool PerformStageAnalysis
        {
            get { return IsVerbose2; }
        }

        public static bool CollectShedulerQueuesStats
        {
            get { return IsVerbose2; }
        }

        public static bool CollectApplicationRequestsStats
        {
            get { return IsInfo; }
        }

        public static bool CollectSerializationStats
        {
            get { return IsVerbose; }
        }
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
