using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableJobs;
using Orleans.Journaling;
using Orleans.Journaling.Json;

namespace Orleans.Hosting;

/// <summary>
/// Extensions for configuring Azure Blob Storage durable jobs.
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
        builder.UseJsonJournalFormat(options => options.AddTypeInfoResolver(DurableJobsJsonContext.Default));
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
        builder.UseJsonJournalFormat(options => options.AddTypeInfoResolver(DurableJobsJsonContext.Default));

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
