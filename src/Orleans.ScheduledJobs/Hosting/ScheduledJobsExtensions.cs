using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration.Internal;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.ScheduledJobs;

namespace Orleans.Hosting;

public static class ScheduledJobsExtensions
{
    public static ISiloBuilder UseInMemoryScheduledJobs(this ISiloBuilder builder)
    {
        return builder.ConfigureServices(services =>
        {
            services.AddScheduledJobs();
            services.AddSingleton<InMemoryJobShardManager>();
            services.AddFromExisting<JobShardManager, InMemoryJobShardManager>();
        });
    }

    public static IServiceCollection AddScheduledJobs(this IServiceCollection services)
    {
        if (services.Any(service => service.ServiceType.Equals(typeof(LocalScheduledJobManager))))
        {
            return services;
        }

        services.AddSingleton<IConfigurationValidator, ScheduledJobsOptionsValidator>();
        services.AddSingleton<LocalScheduledJobManager>();
        services.AddFromExisting<ILocalScheduledJobManager, LocalScheduledJobManager>();
        services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, LocalScheduledJobManager>();
        services.AddKeyedTransient<IGrainExtension>(typeof(IScheduledJobReceiverExtension), (sp, _) =>
        {
            var grainContextAccessor = sp.GetRequiredService<IGrainContextAccessor>();
            return new ScheduledJobReceiverExtension(grainContextAccessor.GrainContext, sp.GetRequiredService<ILogger<ScheduledJobReceiverExtension>>());
        });

        return services;
    }
}
