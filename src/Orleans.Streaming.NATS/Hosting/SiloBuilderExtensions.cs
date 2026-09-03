using System;
using Orleans.Hosting;

namespace Orleans.Streaming.NATS.Hosting;

/// <summary>
/// Provides extensions for configuring NATS JetStream providers on an Orleans silo.
/// </summary>
public static class SiloBuilderExtensions
{
    /// <summary>
    /// Configures the silo to use a NATS JetStream provider.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The stream provider name.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    /// <returns>The same <paramref name="builder"/> instance.</returns>
    public static ISiloBuilder AddNatsStreams(this ISiloBuilder builder, string name,
        Action<NatsOptions> configureOptions)
    {
        builder.AddNatsStreams(name, b =>
            b.ConfigureNats(ob => ob.Configure(configureOptions)));
        return builder;
    }

    /// <summary>
    /// Configures the silo to use a NATS JetStream provider.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The stream provider name.</param>
    /// <param name="configure">The delegate used to configure the provider, or <see langword="null"/> to use default settings.</param>
    /// <returns>The same <paramref name="builder"/> instance.</returns>
    public static ISiloBuilder AddNatsStreams(this ISiloBuilder builder, string name,
        Action<SiloNatsStreamConfigurator>? configure)
    {
        var configurator = new SiloNatsStreamConfigurator(name,
            configureServicesDelegate => builder.ConfigureServices(configureServicesDelegate));
        configure?.Invoke(configurator);
        return builder;
    }
}