using System;
using Orleans.Hosting;
using Orleans.Streaming.NATS.Hosting;

namespace Orleans.Streaming.NATS.Hosting;

/// <summary>
/// Provides extensions for configuring NATS JetStream providers on an Orleans client.
/// </summary>
public static class ClientBuilderExtensions
{
    /// <summary>
    /// Configures the client to use a NATS JetStream provider.
    /// </summary>
    /// <param name="builder">The client builder.</param>
    /// <param name="name">The stream provider name.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    /// <returns>The same <paramref name="builder"/> instance.</returns>
    public static IClientBuilder AddNatsStreams(this IClientBuilder builder, string name,
        Action<NatsOptions> configureOptions)
    {
        builder.AddNatsStreams(name, b =>
            b.ConfigureNats(ob => ob.Configure(configureOptions)));
        return builder;
    }

    /// <summary>
    /// Configures the client to use a NATS JetStream provider.
    /// </summary>
    /// <param name="builder">The client builder.</param>
    /// <param name="name">The stream provider name.</param>
    /// <param name="configure">The delegate used to configure the provider, or <see langword="null"/> to use default settings.</param>
    /// <returns>The same <paramref name="builder"/> instance.</returns>
    public static IClientBuilder AddNatsStreams(this IClientBuilder builder, string name,
        Action<ClusterClientNatsStreamConfigurator>? configure)
    {
        var configurator = new ClusterClientNatsStreamConfigurator(name, builder);
        configure?.Invoke(configurator);
        return builder;
    }
}