using System;
using Orleans.Configuration;

namespace Orleans.Hosting;

public static class SiloBuilderExtensions
{
    /// <summary>
    /// Configure silo to use Redis streams.
    /// </summary>
    public static ISiloBuilder AddRedisStreams(this ISiloBuilder builder, string name, Action<RedisStreamingOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(name);

        builder.AddRedisStreams(name, configurator =>
            configurator.ConfigureRedis(builder => builder.Configure(configureOptions)));
        return builder;
    }

    /// <summary>
    /// Configure silo to use Redis streams.
    /// </summary>
    public static ISiloBuilder AddRedisStreams(this ISiloBuilder builder, string name, Action<SiloRedisStreamConfigurator> configure)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);

        var configurator = new SiloRedisStreamConfigurator(name,
            configureServicesDelegate => builder.ConfigureServices(configureServicesDelegate));
        configure.Invoke(configurator);
        return builder;
    }
}
