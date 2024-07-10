namespace Orleans.Hosting;

/// <summary>
/// Allows configuration of individual ADO.NET streams in a silo.
/// </summary>
public static class SiloBuilderAdoNetStreamExtensions
{
    /// <summary>
    /// Configure silo to use ADO.NET persistent streams.
    /// </summary>
    public static ISiloBuilder AddAdoNetStreams(this ISiloBuilder builder, string name, Action<AdoNetStreamOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configureOptions);

        return builder.AddAdoNetStreams(name, b =>
        {
            b.ConfigureAdoNet(ob => ob.Configure(configureOptions));
        });
    }

    /// <summary>
    /// Configure silo to use ADO.NET persistent streams.
    /// </summary>
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