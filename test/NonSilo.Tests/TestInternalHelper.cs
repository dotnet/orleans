using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Scheduler;
using Orleans.Statistics;

namespace UnitTests.TesterInternal
{
    public class TestInternalHelper
    {
        internal static OrleansTaskScheduler InitializeSchedulerForTesting(ISchedulingContext context, IHostEnvironmentStatistics hostStatistics, ILoggerFactory loggerFactory)
        {
            StatisticsCollector.StatisticsCollectionLevel = StatisticsLevel.Info;
            SchedulerStatisticsGroup.Init(loggerFactory);
            var scheduler = OrleansTaskScheduler.CreateTestInstance(4, hostStatistics, loggerFactory);
            scheduler.Start();
            WorkItemGroup ignore = scheduler.RegisterWorkContext(context);
            return scheduler;
        }
    }
}
