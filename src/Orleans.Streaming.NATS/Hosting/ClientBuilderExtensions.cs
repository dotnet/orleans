using System;
using Orleans.Hosting;
using Orleans.Streaming.NATS.Hosting;

namespace Orleans.Streaming.NATS.Hosting;

public static class ClientBuilderExtensions
{
    /// <summary>
    /// Configure cluster client to use NATS persistent streams with default settings
    /// </summary>
    public static IClientBuilder AddNatsStreams(this IClientBuilder builder, string name,
        Action<NatsOptions> configureOptions)
    {
        builder.AddNatsStreams(name, b =>
            b.ConfigureNats(ob => ob.Configure(configureOptions)));
        return builder;
    }

    /// <summary>
    /// Configure cluster client to use NATS persistent streams.
    /// </summary>
    public static IClientBuilder AddNatsStreams(this IClientBuilder builder, string name,
        Action<ClusterClientNatsStreamConfigurator>? configure)
    {
        var configurator = new ClusterClientNatsStreamConfigurator(name, builder);
        configure?.Invoke(configurator);
        return builder;
    }
}