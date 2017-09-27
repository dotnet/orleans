using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Scheduler;

namespace UnitTests.TesterInternal
{
    public class TestInternalHelper
    {
        internal static OrleansTaskScheduler InitializeSchedulerForTesting(ISchedulingContext context, ICorePerformanceMetrics performanceMetrics, ILoggerFactory loggerFactory)
        {
            StatisticsCollector.StatisticsCollectionLevel = StatisticsLevel.Info;
            SchedulerStatisticsGroup.Init(loggerFactory);
            var scheduler = OrleansTaskScheduler.CreateTestInstance(4, performanceMetrics, loggerFactory);
            scheduler.Start();
            WorkItemGroup ignore = scheduler.RegisterWorkContext(context);
            return scheduler;
        }
    }
}
