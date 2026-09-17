using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration.Internal;
using Orleans.Runtime;
using Orleans.DurableJobs;
using Orleans.Journaling;
using Orleans.Journaling.Json;
using Orleans.Configuration;

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

        if (services.Any(service => service.ServiceType == typeof(IDurableJobHandlerRegistry)))
        {
            throw new InvalidOperationException(
                $"{nameof(IDurableJobHandlerRegistry)} is DurableJobs infrastructure and cannot be replaced or decorated.");
        }

        services.TryAddSingleton<DurableJobsInstruments>();
        services.AddSingleton<IConfigurationValidator, DurableJobsOptionsValidator>();
        services.AddSingleton<IConfigurationValidator, DurableJobsJournalingConfigurationValidator>();
        services.AddSingleton<ShardExecutor>();
        services.AddSingleton<LocalDurableJobManager>();
        services.AddFromExisting<ILocalDurableJobManager, LocalDurableJobManager>();
        services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, LocalDurableJobManager>();
        services.AddScoped<IDurableJobHandlerRegistry, DurableJobHandlerRegistry>();
        services.AddSingleton(sp => new DurableJobReceiverExtensionShared(
            sp.GetRequiredService<ILogger<DurableJobReceiverExtension>>(),
            sp.GetRequiredService<IOptions<DurableJobsOptions>>(),
            sp.GetRequiredService<IOptions<SiloMessagingOptions>>(),
            sp.GetKeyedService<TimeProvider>(DurableJobTimeProviderNames.DurableJobs),
            sp.GetRequiredService<DurableJobsInstruments>()));
        services.AddKeyedTransient<IGrainExtension>(typeof(IDurableJobReceiverExtension), (sp, _) =>
        {
            var grainContextAccessor = sp.GetRequiredService<IGrainContextAccessor>();
            var registry = sp.GetRequiredService<IDurableJobHandlerRegistry>();
            if (registry is not DurableJobHandlerRegistry lookup)
            {
                throw new InvalidOperationException(
                    $"{nameof(IDurableJobHandlerRegistry)} is DurableJobs infrastructure and cannot be replaced or decorated.");
            }

            return new DurableJobReceiverExtension(
                grainContextAccessor.GrainContext,
                sp.GetRequiredService<DurableJobReceiverExtensionShared>(),
                lookup);
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
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddVolatileJournalStorage().UseJournaledDurableJobs();
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
        new ServiceCollectionSiloBuilder(services).UseInMemoryDurableJobs();
        return services;
    }

    /// <summary>
    /// Configures Durable Jobs to create shards in the selected write journal provider
    /// and drain existing shards from all selected providers.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configure">The optional Durable Jobs configuration delegate.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder UseJournaledDurableJobs(this ISiloBuilder builder, Action<DurableJobsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddDurableJobs();
        builder.AddJournalStorage();
        builder.Configure<JsonJournalOptions>(options => options.AddTypeInfoResolver(DurableJobsJsonContext.Default));
        if (configure is not null)
        {
            builder.Services.Configure(configure);
        }

        var services = builder.Services;
        services.TryAddSingleton<DurableJobsJournalProviders>();
        services.TryAddSingleton(sp => new JournaledJobShardManager(
            sp.GetRequiredService<ILocalSiloDetails>(),
            sp.GetRequiredService<DurableJobsJournalProviders>(),
            sp.GetRequiredService<IClusterMembershipService>(),
            sp,
            sp.GetRequiredService<IOptions<DurableJobsOptions>>(),
            sp.GetRequiredService<IOptions<JournaledStateManagerOptions>>(),
            sp.GetRequiredService<DurableJobsInstruments>(),
            sp.GetRequiredService<ILogger<JournaledJobShardManager>>()));
        services.TryAddFromExisting<JobShardManager, JournaledJobShardManager>();
        services.TryAddSingleton<IDurableJobsStorageInspector, DurableJobsStorageInspector>();
        return builder;
    }

    /// <summary>
    /// Configures Durable Jobs to use the selected named journal providers.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">The optional Durable Jobs configuration delegate.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection UseJournaledDurableJobs(this IServiceCollection services, Action<DurableJobsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        new ServiceCollectionSiloBuilder(services).UseJournaledDurableJobs(configure);
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
