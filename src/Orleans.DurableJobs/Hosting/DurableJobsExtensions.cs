using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Orleans.Configuration.Internal;
using Orleans.Runtime;
using Orleans.DurableJobs;
using Orleans.Journaling;
using Orleans.Journaling.Json;

namespace Orleans.Hosting;

/// <summary>
/// Extensions to <see cref="ISiloBuilder"/> for configuring durable jobs.
/// </summary>
public static class DurableJobsExtensions
{
    /// <summary>
    /// Adds support for durable jobs to this silo.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder AddDurableJobs(this ISiloBuilder builder) => builder.ConfigureServices(services => AddDurableJobs(services));

    /// <summary>
    /// Adds support for durable jobs to this silo.
    /// </summary>
    /// <param name="services">The services.</param>
    public static void AddDurableJobs(this IServiceCollection services)
    {
        if (services.Any(service => service.ServiceType.Equals(typeof(LocalDurableJobManager))))
        {
            return;
        }

        services.AddSingleton<IConfigurationValidator, DurableJobsOptionsValidator>();
        services.AddSingleton<IConfigurationValidator, DurableJobsJournalingConfigurationValidator>();
        services.AddSingleton<ShardExecutor>();
        services.AddSingleton<LocalDurableJobManager>();
        services.AddFromExisting<ILocalDurableJobManager, LocalDurableJobManager>();
        services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, LocalDurableJobManager>();
        services.AddKeyedTransient<IGrainExtension>(typeof(IDurableJobReceiverExtension), (sp, _) =>
        {
            var grainContextAccessor = sp.GetRequiredService<IGrainContextAccessor>();
            return new DurableJobReceiverExtension(grainContextAccessor.GrainContext, sp.GetRequiredService<ILogger<DurableJobReceiverExtension>>());
        });
    }

    /// <summary>
    /// Configures durable jobs storage using an in-memory, non-persistent store.
    /// </summary>
    /// <remarks>
    /// Note that this is for development and testing scenarios only and should not be used in production.
    /// </remarks>
    /// <param name="builder">The silo host builder.</param>
    /// <returns>The provided <see cref="ISiloBuilder"/>, for chaining.</returns>
    public static ISiloBuilder UseInMemoryDurableJobs(this ISiloBuilder builder)
    {
        builder.AddDurableJobs();
        builder.AddJournalStorage();
        builder.UseJsonJournalFormat(options => options.AddTypeInfoResolver(DurableJobsJsonContext.Default));

        builder.ConfigureServices(services => services.UseVolatileJournaledDurableJobs());
        return builder;
    }

    /// <summary>
    /// Configures durable jobs storage using an in-memory, non-persistent store.
    /// </summary>
    /// <remarks>
    /// Note that this is for development and testing scenarios only and should not be used in production.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <returns>The provided <see cref="IServiceCollection"/>, for chaining.</returns>
    internal static IServiceCollection UseInMemoryDurableJobs(this IServiceCollection services)
    {
        var builder = new ServiceCollectionSiloBuilder(services);
        builder.AddJournalStorage();
        builder.UseJsonJournalFormat(options => options.AddTypeInfoResolver(DurableJobsJsonContext.Default));
        return services.UseVolatileJournaledDurableJobs();
    }

    private static IServiceCollection UseVolatileJournaledDurableJobs(this IServiceCollection services)
    {
        services.TryAddSingleton<VolatileJournalStorageProvider>();
        services.AddFromExisting<IJournalStorageProvider, VolatileJournalStorageProvider>();
        services.AddFromExisting<IJournalStorageCatalog, VolatileJournalStorageProvider>();
        services.TryAddSingleton<JournaledJobShardManager>();
        services.AddFromExisting<JobShardManager, JournaledJobShardManager>();
        return services;
    }

    private sealed class ServiceCollectionSiloBuilder : ISiloBuilder
    {
        public ServiceCollectionSiloBuilder(IServiceCollection services)
        {
            Services = services;
            Configuration = new ConfigurationBuilder().Build();
        }

        public IServiceCollection Services { get; }

        public IConfiguration Configuration { get; }
    }
}
