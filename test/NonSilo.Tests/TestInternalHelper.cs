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
            IGrainContext context,
            ILoggerFactory loggerFactory)
        {
            var services = new ServiceCollection();
            services.AddOptions();
            services.AddLogging();
            services.AddSingleton<SchedulerStatisticsGroup>();
            services.AddSingleton<StageAnalysisStatisticsGroup>();
            services.AddSingleton(loggerFactory);
            services.Configure<SchedulingOptions>(options =>
            {
                options.MaxActiveThreads = 4;
                options.DelayWarningThreshold = TimeSpan.FromMilliseconds(100);
                options.ActivationSchedulingQuantum = TimeSpan.FromMilliseconds(100);
                options.TurnWarningLengthThreshold = TimeSpan.FromMilliseconds(100);
                options.StoppedActivationWarningInterval = TimeSpan.FromMilliseconds(200);
            });

            var serviceProvider = services.BuildServiceProvider();

            var scheduler = ActivatorUtilities.CreateInstance<OrleansTaskScheduler>(serviceProvider);
            WorkItemGroup ignore = scheduler.RegisterWorkContext(context);
            return scheduler;
        }
    }
}
