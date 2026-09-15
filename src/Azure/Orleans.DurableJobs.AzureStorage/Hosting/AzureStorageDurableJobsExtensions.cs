using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Configuration.Internal;
using Orleans.DurableJobs;
using Orleans.Journaling;
using Orleans.Journaling.Json;

namespace Orleans.Hosting;

/// <summary>
/// Extensions for configuring Azure Storage durable jobs.
/// </summary>
public static class AzureStorageDurableJobsExtensions
{
    /// <summary>
    /// Adds durable jobs storage backed by Azure Blob Storage.
    /// </summary>
    /// <param name="builder">
    /// The builder.
    /// </param>
    /// <param name="configure">
    /// The delegate used to configure the durable jobs storage.
    /// </param>
    /// <returns>
    /// The provided <see cref="ISiloBuilder"/>, for chaining.
    /// </returns>
    public static ISiloBuilder UseAzureBlobDurableJobs(this ISiloBuilder builder, Action<AzureBlobJournalStorageOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        builder.AddDurableJobs();
        builder.AddAzureBlobJournalStorage(configure);
        builder.Configure<JsonJournalOptions>(options => options.AddTypeInfoResolver(DurableJobsJsonContext.Default));
        builder.Services.UseJournaledDurableJobs();
        return builder;
    }

    /// <summary>
    /// Adds durable jobs storage backed by Azure Blob Storage.
    /// </summary>
    /// <param name="services">
    /// The service collection.
    /// </param>
    /// <param name="configure">
    /// The delegate used to configure the durable jobs storage.
    /// </param>
    /// <returns>
    /// The provided <see cref="IServiceCollection"/>, for chaining.
    /// </returns>
    public static IServiceCollection UseAzureBlobDurableJobs(this IServiceCollection services, Action<AzureBlobJournalStorageOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddDurableJobs();

        var builder = new ServiceCollectionSiloBuilder(services);
        builder.AddAzureBlobJournalStorage(configure);
        builder.Configure<JsonJournalOptions>(options => options.AddTypeInfoResolver(DurableJobsJsonContext.Default));

        services.UseJournaledDurableJobs();
        return services;
    }

    /// <summary>
    /// Adds durable jobs backed by Azure Table journal storage.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configure">The delegate used to configure journal storage.</param>
    /// <returns>The silo builder, for chaining.</returns>
    public static ISiloBuilder UseAzureTableDurableJobs(this ISiloBuilder builder, Action<AzureTableJournalStorageOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        builder.AddDurableJobs();
        builder.AddAzureTableJournalStorage(configure);
        builder.Configure<JsonJournalOptions>(options => options.AddTypeInfoResolver(DurableJobsJsonContext.Default));
        builder.Services.UseJournaledDurableJobs();
        return builder;
    }

    /// <summary>
    /// Adds durable jobs backed by Azure Table journal storage.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">The delegate used to configure journal storage.</param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection UseAzureTableDurableJobs(this IServiceCollection services, Action<AzureTableJournalStorageOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        new ServiceCollectionSiloBuilder(services).UseAzureTableDurableJobs(configure);
        return services;
    }

    private static IServiceCollection UseJournaledDurableJobs(this IServiceCollection services)
    {
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
