using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;

namespace Orleans.Hosting;

/// <summary>
/// Extensions to <see cref="IClientBuilder"/> for configuring Redis streams.
/// </summary>
public static class ClientBuilderExtensions
{
    /// <summary>
    /// Configure cluster client to use Redis streams.
    /// </summary>
    public static IClientBuilder AddRedisStreams(this IClientBuilder builder, string name, Action<RedisStreamingOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(name);

        builder.AddRedisStreams(name, configurator =>
            configurator.ConfigureRedis(builder => builder.Configure(configureOptions)));
        return builder;
    }

    /// <summary>
    /// Configure cluster client to use Redis streams.
    /// </summary>
    public static IClientBuilder AddRedisStreams(this IClientBuilder builder, string name, Action<IServiceProvider, RedisStreamingOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configureOptions);

        builder.AddRedisStreams(name, configurator =>
            configurator.ConfigureRedis(optionsBuilder => optionsBuilder.Configure<IServiceProvider>((options, services) => configureOptions(services, options))));
        return builder;
    }

    /// <summary>
    /// Configure cluster client to use Redis streams.
    /// </summary>
    public static IClientBuilder AddRedisStreams(this IClientBuilder builder, string name, Action<ClusterClientRedisStreamConfigurator> configure)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);

        var configurator = new ClusterClientRedisStreamConfigurator(name, builder);
        configure.Invoke(configurator);
        return builder;
    }

    /// <summary>
    /// Configure cluster client to use Redis streams and register supporting services.
    /// </summary>
    /// <remarks>
    /// This overload accepts <see cref="IServiceCollection"/> rather than <see cref="IServiceProvider"/>
    /// because the service provider has not been built yet when the configurator is invoked.
    /// </remarks>
    public static IClientBuilder AddRedisStreams(this IClientBuilder builder, string name, Action<IServiceCollection, ClusterClientRedisStreamConfigurator> configure)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);

        var configurator = new ClusterClientRedisStreamConfigurator(name, builder);
        configure.Invoke(builder.Services, configurator);
        return builder;
    }
}
