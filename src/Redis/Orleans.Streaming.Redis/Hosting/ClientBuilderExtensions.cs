using System;
namespace Orleans.Hosting;

/// <summary>
/// Extensions to <see cref="IClientBuilder"/> for configuring Redis streams.
/// </summary>
public static class ClientBuilderExtensions
{
    /// <summary>
    /// Configures the client to use a Redis stream provider.
    /// </summary>
    /// <param name="builder">The client builder.</param>
    /// <param name="name">The stream provider name.</param>
    /// <param name="configure">The delegate used to configure the provider.</param>
    /// <returns>The same <paramref name="builder"/> instance.</returns>
    public static IClientBuilder AddRedisStreams(this IClientBuilder builder, string name, Action<ClusterClientRedisStreamConfigurator> configure)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);

        var configurator = new ClusterClientRedisStreamConfigurator(name, builder);
        configure.Invoke(configurator);
        return builder;
    }
}
