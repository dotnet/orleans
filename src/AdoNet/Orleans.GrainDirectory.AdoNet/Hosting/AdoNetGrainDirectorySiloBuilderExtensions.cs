namespace Orleans.Hosting;

/// <summary>
/// Extension methods for configuring an ADO.NET grain directory.
/// </summary>
public static class AdoNetGrainDirectorySiloBuilderExtensions
{
    /// <summary>
    /// Configures ADO.NET as the default grain directory.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder UseAdoNetGrainDirectoryAsDefault(
        this ISiloBuilder builder,
        Action<AdoNetGrainDirectoryOptions> configureOptions) =>
        builder.UseAdoNetGrainDirectoryAsDefault(ob => ob.Configure(configureOptions));

    /// <summary>
    /// Configures ADO.NET as the default grain directory.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configureOptions">The delegate used to configure the provider options builder.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder UseAdoNetGrainDirectoryAsDefault(
        this ISiloBuilder builder,
        Action<OptionsBuilder<AdoNetGrainDirectoryOptions>> configureOptions) =>
        builder.ConfigureServices(services => services.AddAdoNetGrainDirectory(GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY, configureOptions));

    /// <summary>
    /// Adds a named ADO.NET grain directory.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The name of the grain directory.</param>
    /// <param name="configureOptions">The delegate used to configure the provider.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder AddAdoNetGrainDirectory(
        this ISiloBuilder builder,
        string name,
        Action<AdoNetGrainDirectoryOptions> configureOptions) =>
        builder.AddAdoNetGrainDirectory(name, ob => ob.Configure(configureOptions));

    /// <summary>
    /// Adds a named ADO.NET grain directory.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The name of the grain directory.</param>
    /// <param name="configureOptions">The delegate used to configure the provider options builder.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder AddAdoNetGrainDirectory(
        this ISiloBuilder builder,
        string name,
        Action<OptionsBuilder<AdoNetGrainDirectoryOptions>> configureOptions) =>
        builder.ConfigureServices(services => services.AddAdoNetGrainDirectory(name, configureOptions));
}
