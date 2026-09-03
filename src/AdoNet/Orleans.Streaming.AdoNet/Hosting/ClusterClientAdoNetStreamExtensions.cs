namespace Orleans.Hosting;

/// <summary>
/// Allows configuration of individual ADO.NET streams in a cluster client.
/// </summary>
public static class ClusterClientAdoNetStreamExtensions
{
    /// <summary>
    /// Configures the client to use an ADO.NET stream provider.
    /// </summary>
    /// <param name="builder">The client builder.</param>
    /// <param name="name">The stream provider name.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    /// <returns>The same <paramref name="builder"/> instance.</returns>
    public static IClientBuilder AddAdoNetStreams(this IClientBuilder builder, string name, Action<AdoNetStreamOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configureOptions);

        return builder.AddAdoNetStreams(name, b => b.ConfigureAdoNet(ob => ob.Configure(configureOptions)));
    }

    /// <summary>
    /// Configures the client to use an ADO.NET stream provider.
    /// </summary>
    /// <param name="builder">The client builder.</param>
    /// <param name="name">The stream provider name.</param>
    /// <param name="configure">The delegate used to configure the provider.</param>
    /// <returns>The same <paramref name="builder"/> instance.</returns>
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