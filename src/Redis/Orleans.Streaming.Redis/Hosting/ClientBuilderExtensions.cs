using System;
using Orleans.Configuration;

namespace Orleans.Hosting;

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
    public static IClientBuilder AddRedisStreams(this IClientBuilder builder, string name, Action<ClusterClientRedisStreamConfigurator> configure)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);

        var configurator = new ClusterClientRedisStreamConfigurator(name, builder);
        configure.Invoke(configurator);
        return builder;
    }
}
