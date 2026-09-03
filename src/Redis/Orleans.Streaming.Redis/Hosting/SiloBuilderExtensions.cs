namespace Orleans.Hosting;

/// <summary>
/// Extensions for <see cref="ISiloBuilder"/> to configure Redis streams.
/// </summary>
public static class SiloBuilderExtensions
{
    /// <summary>
    /// Configures the silo to use a Redis stream provider.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The stream provider name.</param>
    /// <param name="configure">The delegate used to configure the provider.</param>
    /// <returns>The same <paramref name="builder"/> instance.</returns>
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
