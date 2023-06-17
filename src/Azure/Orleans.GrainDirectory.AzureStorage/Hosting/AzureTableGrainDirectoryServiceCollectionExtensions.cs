using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.GrainDirectory;
using Orleans.GrainDirectory.AzureStorage;
using Orleans.Runtime;

namespace Orleans.Hosting
{
    /// <summary>
    /// <see cref="IServiceCollection"/> extensions.
    /// </summary>
    public static class AzureTableGrainDirectoryServiceCollectionExtensions
    {
        internal static IServiceCollection AddAzureTableGrainDirectory(
            this IServiceCollection services,
            string name,
            Action<OptionsBuilder<AzureTableGrainDirectoryOptions>> configureOptions)
        {
            configureOptions.Invoke(services.AddOptions<AzureTableGrainDirectoryOptions>(name));
            services
                .AddTransient<IConfigurationValidator>(sp => new AzureTableGrainDirectoryOptionsValidator(sp.GetRequiredService<IOptionsMonitor<AzureTableGrainDirectoryOptions>>().Get(name), name))
                .ConfigureNamedOptionForLogging<AzureTableGrainDirectoryOptions>(name)
                .AddSingletonNamedService<IGrainDirectory>(name, (sp, name) => ActivatorUtilities.CreateInstance<AzureTableGrainDirectory>(sp, sp.GetOptionsByName<AzureTableGrainDirectoryOptions>(name)))
                .AddSingletonNamedService<ILifecycleParticipant<ISiloLifecycle>>(name, (s, n) => (ILifecycleParticipant<ISiloLifecycle>)s.GetRequiredServiceByName<IGrainDirectory>(n));

            return services;
        }
    }
}
