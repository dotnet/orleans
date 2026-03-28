using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Hosting;

public static class SiloBuilderExtensions
{
    /// <summary>
    /// Configure silo to use Redis persistent streams.
    /// </summary>
    public static ISiloBuilder AddRedisStreams(this ISiloBuilder builder, string name, Action<RedisStreamOptions> configureOptions)
    {
        builder.AddRedisStreams(name, ob =>
            ob.Configure(configureOptions));
        return builder;
    }

    /// <summary>
    /// Configure silo to use Redis persistent streams.
    /// </summary>
    public static ISiloBuilder AddRedisStreams(this ISiloBuilder builder, string name, Action<OptionsBuilder<RedisStreamOptions>> configureOptionsBuilder)
    {
        builder.AddRedisStreams(name, cb =>
            cb.ConfigureRedis(configureOptionsBuilder));
        return builder;
    }

    /// <summary>
    /// Configure silo to use Redis persistent streams.
    /// </summary>
    public static ISiloBuilder AddRedisStreams(this ISiloBuilder builder, string name, Action<SiloRedisStreamConfigurator> configure)
    {
        var configurator = new SiloRedisStreamConfigurator(name, builder);
        configure?.Invoke(configurator);
        configurator.PostConfigureComponents();

        return builder;
    }
}
