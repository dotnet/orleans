using System;
using Orleans.Hosting;

namespace Orleans.Streaming.NATS.Hosting;

public static class SiloBuilderExtensions
{
    /// <summary>
    /// Configure silo to use NATS persistent streams.
    /// </summary>
    public static ISiloBuilder AddNatsStreams(this ISiloBuilder builder, string name,
        Action<NatsOptions> configureOptions)
    {
        builder.AddNatsStreams(name, b =>
            b.ConfigureNats(ob => ob.Configure(configureOptions)));
        return builder;
    }

    /// <summary>
    /// Configure silo to use NATS persistent streams.
    /// </summary>
    public static ISiloBuilder AddNatsStreams(this ISiloBuilder builder, string name,
        Action<SiloNatsStreamConfigurator>? configure)
    {
        var configurator = new SiloNatsStreamConfigurator(name,
            configureServicesDelegate => builder.ConfigureServices(configureServicesDelegate));
        configure?.Invoke(configurator);
        return builder;
    }
}