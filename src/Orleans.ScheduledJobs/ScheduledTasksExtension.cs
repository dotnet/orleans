using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration.Internal;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.ScheduledJobs;

namespace Orleans.Hosting;

public static class  ScheduledJobsExtension
{
    public static ISiloBuilder UseInMemoryScheduledJobs(this ISiloBuilder builder)
    {
        return builder.ConfigureServices(services =>
        {
            services.AddSingleton<InMemoryJobShardManager>();
            services.AddFromExisting<JobShardManager, InMemoryJobShardManager>();
            services.AddSingleton<LocalScheduledJobManager>();
            services.AddFromExisting<ILocalScheduledJobManager, LocalScheduledJobManager>();
            services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, LocalScheduledJobManager>();
        });
    }
}
