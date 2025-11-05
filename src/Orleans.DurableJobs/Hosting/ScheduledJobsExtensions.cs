using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration.Internal;
using Orleans.Runtime;
using Orleans.DurableJobs;

namespace Orleans.Hosting;

/// <summary>
/// Extensions to <see cref="ISiloBuilder"/> for configuring scheduled jobs.
/// </summary>
public static class ScheduledJobsExtensions
{
    /// <summary>
    /// Adds support for scheduled jobs to this silo.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder AddScheduledJobs(this ISiloBuilder builder) => builder.ConfigureServices(services => AddScheduledJobs(services));

    /// <summary>
    /// Adds support for scheduled jobs to this silo.
    /// </summary>
    /// <param name="services">The services.</param>
    public static void AddScheduledJobs(this IServiceCollection services)
    {
        if (services.Any(service => service.ServiceType.Equals(typeof(LocalScheduledJobManager))))
        {
            return;
        }

        services.AddSingleton<IConfigurationValidator, ScheduledJobsOptionsValidator>();
        services.AddSingleton<ShardExecutor>();
        services.AddSingleton<LocalScheduledJobManager>();
        services.AddFromExisting<ILocalScheduledJobManager, LocalScheduledJobManager>();
        services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, LocalScheduledJobManager>();
        services.AddKeyedTransient<IGrainExtension>(typeof(IScheduledJobReceiverExtension), (sp, _) =>
        {
            var grainContextAccessor = sp.GetRequiredService<IGrainContextAccessor>();
            return new ScheduledJobReceiverExtension(grainContextAccessor.GrainContext, sp.GetRequiredService<ILogger<ScheduledJobReceiverExtension>>());
        });
    }

    /// <summary>
    /// Configures scheduled jobs storage using an in-memory, non-persistent store.
    /// </summary>
    /// <remarks>
    /// Note that this is for development and testing scenarios only and should not be used in production.
    /// </remarks>
    /// <param name="builder">The silo host builder.</param>
    /// <returns>The provided <see cref="ISiloBuilder"/>, for chaining.</returns>
    public static ISiloBuilder UseInMemoryScheduledJobs(this ISiloBuilder builder)
    {
        builder.AddScheduledJobs();

        builder.ConfigureServices(services => services.UseInMemoryScheduledJobs());
        return builder;
    }

    /// <summary>
    /// Configures scheduled jobs storage using an in-memory, non-persistent store.
    /// </summary>
    /// <remarks>
    /// Note that this is for development and testing scenarios only and should not be used in production.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <returns>The provided <see cref="IServiceCollection"/>, for chaining.</returns>
    internal static IServiceCollection UseInMemoryScheduledJobs(this IServiceCollection services)
    {
        services.AddSingleton<InMemoryJobShardManager>(sp =>
        {
            var siloDetails = sp.GetRequiredService<ILocalSiloDetails>();
            var membershipService = sp.GetRequiredService<IClusterMembershipService>();
            return new InMemoryJobShardManager(siloDetails.SiloAddress, membershipService);
        });
        services.AddFromExisting<JobShardManager, InMemoryJobShardManager>();
        return services;
    }
}
