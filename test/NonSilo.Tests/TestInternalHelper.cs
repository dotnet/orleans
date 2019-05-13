using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Scheduler;
using Orleans.Statistics;

namespace UnitTests.TesterInternal
{
    public class TestInternalHelper
    {
        internal static OrleansTaskScheduler InitializeSchedulerForTesting(
            ISchedulingContext context,
            IHostEnvironmentStatistics hostStatistics,
            ILoggerFactory loggerFactory)
        {
            var services = new ServiceCollection();
            services.AddOptions();
            services.AddLogging();
            services.AddSingleton<ExecutorService>();
            services.AddSingleton<SchedulerStatisticsGroup>();
            services.AddSingleton<StageAnalysisStatisticsGroup>();
            services.AddSingleton(loggerFactory);
            services.Configure<SchedulingOptions>(options =>
            {
                options.MaxActiveThreads = 4;
                options.DelayWarningThreshold = TimeSpan.FromMilliseconds(100);
                options.ActivationSchedulingQuantum = TimeSpan.FromMilliseconds(100);
                options.TurnWarningLengthThreshold = TimeSpan.FromMilliseconds(100);
            });

            var serviceProvider = services.BuildServiceProvider();

            var scheduler = ActivatorUtilities.CreateInstance<OrleansTaskScheduler>(serviceProvider);
            scheduler.Start();
            WorkItemGroup ignore = scheduler.RegisterWorkContext(context);
            return scheduler;
        }
    }
}
