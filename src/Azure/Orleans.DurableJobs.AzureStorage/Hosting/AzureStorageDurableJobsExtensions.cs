using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Configuration.Internal;
using Orleans.DurableJobs;
using Orleans.DurableJobs.AzureStorage;

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
    public static ISiloBuilder UseAzureBlobDurableJobs(this ISiloBuilder builder, Action<AzureStorageJobShardOptions> configure)
    {
        builder.ConfigureServices(services => services.UseAzureBlobDurableJobs(configure));
        return builder;
    }

    /// <summary>
    /// Adds durable jobs storage backed by Azure Blob Storage.
    /// </summary>
    /// <param name="builder">
    /// The builder.
    /// </param>
    /// <param name="configureOptions">
    /// The configuration delegate.
    /// </param>
    /// <returns>
    /// The provided <see cref="ISiloBuilder"/>, for chaining.
    /// </returns>
    public static ISiloBuilder UseAzureBlobDurableJobs(this ISiloBuilder builder, Action<OptionsBuilder<AzureStorageJobShardOptions>> configureOptions)
    {
        builder.ConfigureServices(services => services.UseAzureBlobDurableJobs(configureOptions));
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
    public static IServiceCollection UseAzureBlobDurableJobs(this IServiceCollection services, Action<AzureStorageJobShardOptions> configure)
    {
        services.AddDurableJobs();
        services.AddSingleton<AzureStorageJobShardManager>();
        services.AddFromExisting<Orleans.DurableJobs.JobShardManager, AzureStorageJobShardManager>();
        services.Configure<AzureStorageJobShardOptions>(configure);
        services.ConfigureFormatter<AzureStorageJobShardOptions>();
        return services;
    }

    /// <summary>
    /// Adds durable jobs storage backed by Azure Blob Storage.
    /// </summary>
    /// <param name="services">
    /// The service collection.
    /// </param>
    /// <param name="configureOptions">
    /// The configuration delegate.
    /// </param>
    /// <returns>
    /// The provided <see cref="IServiceCollection"/>, for chaining.
    /// </returns>
    public static IServiceCollection UseAzureBlobDurableJobs(this IServiceCollection services, Action<OptionsBuilder<AzureStorageJobShardOptions>> configureOptions)
    {
        services.AddDurableJobs();
        services.AddSingleton<AzureStorageJobShardManager>();
        services.AddFromExisting<Orleans.DurableJobs.JobShardManager, AzureStorageJobShardManager>();
        configureOptions?.Invoke(services.AddOptions<AzureStorageJobShardOptions>());
        services.ConfigureFormatter<AzureStorageJobShardOptions>();
        services.AddTransient<IConfigurationValidator>(sp => new AzureStorageJobShardOptionsValidator(sp.GetRequiredService<IOptionsMonitor<AzureStorageJobShardOptions>>().Get(Options.DefaultName), Options.DefaultName));
        return services;
    }
}
