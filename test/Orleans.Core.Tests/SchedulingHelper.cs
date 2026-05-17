using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Scheduler;

namespace UnitTests.TesterInternal
{
    public class SchedulingHelper
    {
        internal static WorkItemGroup CreateWorkItemGroupForTesting(
            IGrainContext context,
            ILoggerFactory loggerFactory,
            out IServiceProvider activationServices)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(loggerFactory);
            var services = new ServiceCollection();
            services.AddOptions();
            services.AddLogging();
            services.AddSingleton(loggerFactory);
            services.Configure<SchedulingOptions>(options =>
            {
                options.DelayWarningThreshold = TimeSpan.FromMilliseconds(100);
                options.ActivationSchedulingQuantum = TimeSpan.FromMilliseconds(100);
                options.TurnWarningLengthThreshold = TimeSpan.FromMilliseconds(100);
                options.StoppedActivationWarningInterval = TimeSpan.FromMilliseconds(200);
            });

            activationServices = services.BuildServiceProvider();
            return new WorkItemGroup(
                context,
                activationServices.GetRequiredService<IOptions<SchedulingOptions>>());
        }
    }
}
