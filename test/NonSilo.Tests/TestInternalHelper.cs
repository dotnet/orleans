using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Scheduler;

namespace UnitTests.TesterInternal
{
    public class TestInternalHelper
    {
        internal static OrleansTaskScheduler InitializeSchedulerForTesting(ISchedulingContext context, ICorePerformanceMetrics performanceMetrics)
        {
            StatisticsCollector.StatisticsCollectionLevel = StatisticsLevel.Info;
            SchedulerStatisticsGroup.Init();
            var scheduler = OrleansTaskScheduler.CreateTestInstance(4, performanceMetrics);
            scheduler.Start();
            WorkItemGroup ignore = scheduler.RegisterWorkContext(context);
            return scheduler;
        }
    }
}
