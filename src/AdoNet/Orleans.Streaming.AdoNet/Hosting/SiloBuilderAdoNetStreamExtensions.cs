namespace Orleans.Hosting;

/// <summary>
/// Allows configuration of individual ADO.NET streams in a silo.
/// </summary>
public static class SiloBuilderAdoNetStreamExtensions
{
    /// <summary>
    /// Configures the silo to use an ADO.NET stream provider.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The stream provider name.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    /// <returns>The same <paramref name="builder"/> instance.</returns>
    public static ISiloBuilder AddAdoNetStreams(this ISiloBuilder builder, string name, Action<AdoNetStreamOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configureOptions);

        return builder.AddAdoNetStreams(name, b => b.ConfigureAdoNet(ob => ob.Configure(configureOptions)));
    }

    /// <summary>
    /// Configures the silo to use an ADO.NET stream provider.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The stream provider name.</param>
    /// <param name="configure">The delegate used to configure the provider.</param>
    /// <returns>The same <paramref name="builder"/> instance.</returns>
    public static ISiloBuilder AddAdoNetStreams(this ISiloBuilder builder, string name, Action<SiloAdoNetStreamConfigurator> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);

        var configurator = new SiloAdoNetStreamConfigurator(name, configureServicesDelegate => builder.ConfigureServices(configureServicesDelegate));

        configure.Invoke(configurator);

        return builder;
    }
}
