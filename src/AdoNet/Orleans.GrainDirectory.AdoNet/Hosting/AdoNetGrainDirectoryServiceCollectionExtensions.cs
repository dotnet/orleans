using Orleans.Runtime.Hosting;

namespace Orleans.Hosting;

/// <summary>
/// <see cref="IServiceCollection"/> extensions.
/// </summary>
internal static class AdoNetGrainDirectoryServiceCollectionExtensions
{
    internal static IServiceCollection AddAdoNetGrainDirectory(
        this IServiceCollection services,
        string name,
        Action<OptionsBuilder<AdoNetGrainDirectoryOptions>> configureOptions)
    {
        configureOptions.Invoke(services.AddOptions<AdoNetGrainDirectoryOptions>(name));

        return services
            .AddTransient<IConfigurationValidator>(sp => new AdoNetGrainDirectoryOptionsValidator(sp.GetRequiredService<IOptionsMonitor<AdoNetGrainDirectoryOptions>>().Get(name), name))
            .ConfigureNamedOptionForLogging<AdoNetGrainDirectoryOptions>(name)
            .AddGrainDirectory(name, (sp, name) =>
            {
                var options = sp.GetOptionsByName<AdoNetGrainDirectoryOptions>(name);

                return ActivatorUtilities.CreateInstance<AdoNetGrainDirectory>(sp, name, options);
            });
    }
}
