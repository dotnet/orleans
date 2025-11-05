using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Configuration.Internal;
using Orleans.ScheduledJobs;
using Orleans.ScheduledJobs.AzureStorage;

namespace Orleans.Hosting;

/// <summary>
/// Extensions for configuring Azure Blob Storage scheduled jobs.
/// </summary>
public static class AzureStorageScheduledJobsExtensions
{
    /// <summary>
    /// Adds scheduled jobs storage backed by Azure Blob Storage.
    /// </summary>
    /// <param name="builder">
    /// The builder.
    /// </param>
    /// <param name="configure">
    /// The delegate used to configure the scheduled jobs storage.
    /// </param>
    /// <returns>
    /// The provided <see cref="ISiloBuilder"/>, for chaining.
    /// </returns>
    public static ISiloBuilder UseAzureBlobScheduledJobs(this ISiloBuilder builder, Action<AzureStorageJobShardOptions> configure)
    {
        builder.ConfigureServices(services => services.UseAzureBlobScheduledJobs(configure));
        return builder;
    }

    /// <summary>
    /// Adds scheduled jobs storage backed by Azure Blob Storage.
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
    public static ISiloBuilder UseAzureBlobScheduledJobs(this ISiloBuilder builder, Action<OptionsBuilder<AzureStorageJobShardOptions>> configureOptions)
    {
        builder.ConfigureServices(services => services.UseAzureBlobScheduledJobs(configureOptions));
        return builder;
    }

    /// <summary>
    /// Adds scheduled jobs storage backed by Azure Blob Storage.
    /// </summary>
    /// <param name="services">
    /// The service collection.
    /// </param>
    /// <param name="configure">
    /// The delegate used to configure the scheduled jobs storage.
    /// </param>
    /// <returns>
    /// The provided <see cref="IServiceCollection"/>, for chaining.
    /// </returns>
    public static IServiceCollection UseAzureBlobScheduledJobs(this IServiceCollection services, Action<AzureStorageJobShardOptions> configure)
    {
        services.AddScheduledJobs();
        services.AddSingleton<AzureStorageJobShardManager>();
        services.AddFromExisting<JobShardManager, AzureStorageJobShardManager>();
        services.Configure<AzureStorageJobShardOptions>(configure);
        services.ConfigureFormatter<AzureStorageJobShardOptions>();
        return services;
    }

    /// <summary>
    /// Adds scheduled jobs storage backed by Azure Blob Storage.
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
    public static IServiceCollection UseAzureBlobScheduledJobs(this IServiceCollection services, Action<OptionsBuilder<AzureStorageJobShardOptions>> configureOptions)
    {
        services.AddScheduledJobs();
        services.AddSingleton<AzureStorageJobShardManager>();
        services.AddFromExisting<JobShardManager, AzureStorageJobShardManager>();
        configureOptions?.Invoke(services.AddOptions<AzureStorageJobShardOptions>());
        services.ConfigureFormatter<AzureStorageJobShardOptions>();
        services.AddTransient<IConfigurationValidator>(sp => new AzureStorageJobShardOptionsValidator(sp.GetRequiredService<IOptionsMonitor<AzureStorageJobShardOptions>>().Get(Options.DefaultName), Options.DefaultName));
        return services;
    }
}
