namespace Orleans.Hosting;

/// <summary>
/// Allows configuration of individual ADO.NET streams in a cluster client.
/// </summary>
public static class ClusterClientAdoNetStreamExtensions
{
    /// <summary>
    /// Configure cluster client to use ADO.NET persistent streams with default settings.
    /// </summary>
    public static IClientBuilder AddAdoNetStreams(this IClientBuilder builder, string name, Action<AdoNetStreamOptions> configureOptions)
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
    /// Configure cluster client to use ADO.NET persistent streams.
    /// </summary>
    public static IClientBuilder AddAdoNetStreams(this IClientBuilder builder, string name, Action<ClusterClientAdoNetStreamConfigurator> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);

        var configurator = new ClusterClientAdoNetStreamConfigurator(name, builder);

        configure.Invoke(configurator);

        return builder;
    }
}