using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration.Internal;
using Orleans.DurableJobs;
using Orleans.DurableJobs.Redis;

namespace Orleans.Hosting;

/// <summary>
/// Extensions for configuring Redis durable jobs.
/// </summary>
public static class RedisDurableJobsExtensions
{
    /// <summary>
    /// Adds durable jobs storage backed by Redis.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="configure">The delegate used to configure the durable jobs storage.</param>
    /// <returns>The provided <see cref="ISiloBuilder"/>, for chaining.</returns>
    public static ISiloBuilder UseRedisDurableJobs(this ISiloBuilder builder, Action<RedisJobShardOptions> configure)
    {
        builder.ConfigureServices(services => services.UseRedisDurableJobs(configure));
        return builder;
    }

    /// <summary>
    /// Adds durable jobs storage backed by Redis.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="configureOptions">The configuration delegate.</param>
    /// <returns>The provided <see cref="ISiloBuilder"/>, for chaining.</returns>
    public static ISiloBuilder UseRedisDurableJobs(this ISiloBuilder builder, Action<OptionsBuilder<RedisJobShardOptions>> configureOptions)
    {
        builder.ConfigureServices(services => services.UseRedisDurableJobs(configureOptions));
        return builder;
    }

    /// <summary>
    /// Adds durable jobs storage backed by Redis.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">The delegate used to configure the durable jobs storage.</param>
    /// <returns>The provided <see cref="IServiceCollection"/>, for chaining.</returns>
    public static IServiceCollection UseRedisDurableJobs(this IServiceCollection services, Action<RedisJobShardOptions> configure)
    {
        services.AddDurableJobs();
        services.AddSingleton<RedisJobShardManager>();
        services.AddFromExisting<JobShardManager, RedisJobShardManager>();
        services.Configure(configure);
        services.ConfigureFormatter<RedisJobShardOptions>();
        return services;
    }

    /// <summary>
    /// Adds durable jobs storage backed by Redis.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">The configuration delegate.</param>
    /// <returns>The provided <see cref="IServiceCollection"/>, for chaining.</returns>
    public static IServiceCollection UseRedisDurableJobs(this IServiceCollection services, Action<OptionsBuilder<RedisJobShardOptions>>? configureOptions)
    {
        services.AddDurableJobs();
        services.AddSingleton<RedisJobShardManager>();
        services.AddFromExisting<JobShardManager, RedisJobShardManager>();
        configureOptions?.Invoke(services.AddOptions<RedisJobShardOptions>());
        services.ConfigureFormatter<RedisJobShardOptions>();
        services.AddTransient<IConfigurationValidator>(sp => new RedisJobShardOptionsValidator(sp.GetRequiredService<IOptionsMonitor<RedisJobShardOptions>>().Get(Options.DefaultName), Options.DefaultName));
        return services;
    }
}
