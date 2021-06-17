using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.GrainDirectory;
using Orleans.GrainDirectory.AzureStorage;
using Orleans.Runtime;

namespace Orleans.Hosting
{
    public static class AzureTableGrainDirectoryExtensions
    {
        public static ISiloBuilder UseAzureTableGrainDirectoryAsDefault(
            this ISiloBuilder builder,
            Action<AzureTableGrainDirectoryOptions> configureOptions)
        {
            return builder.UseAzureTableGrainDirectoryAsDefault(ob => ob.Configure(configureOptions));
        }

        public static ISiloBuilder UseAzureTableGrainDirectoryAsDefault(
            this ISiloBuilder builder,
            Action<OptionsBuilder<AzureTableGrainDirectoryOptions>> configureOptions)
        {
            return builder.ConfigureServices(services => services.AddAzureTableGrainDirectory(GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY, configureOptions));
        }

        public static ISiloBuilder AddAzureTableGrainDirectory(
            this ISiloBuilder builder,
            string name,
            Action<AzureTableGrainDirectoryOptions> configureOptions)
        {
            return builder.AddAzureTableGrainDirectory(name, ob => ob.Configure(configureOptions));
        }

        public static ISiloBuilder AddAzureTableGrainDirectory(
            this ISiloBuilder builder,
            string name,
            Action<OptionsBuilder<AzureTableGrainDirectoryOptions>> configureOptions)
        {
            return builder.ConfigureServices(services => services.AddAzureTableGrainDirectory(name, configureOptions));
        }

        private static IServiceCollection AddAzureTableGrainDirectory(
            this IServiceCollection services,
            string name,
            Action<OptionsBuilder<AzureTableGrainDirectoryOptions>> configureOptions)
        {
            configureOptions.Invoke(services.AddOptions<AzureTableGrainDirectoryOptions>(name));
            services
                .AddTransient<IConfigurationValidator>(sp => new AzureTableGrainDirectoryOptionsValidator(sp.GetRequiredService<IOptionsMonitor<AzureTableGrainDirectoryOptions>>().Get(name)))
                .ConfigureNamedOptionForLogging<AzureTableGrainDirectoryOptions>(name)
                .AddSingletonNamedService<IGrainDirectory>(name, (sp, name) => ActivatorUtilities.CreateInstance<AzureTableGrainDirectory>(sp, sp.GetOptionsByName<AzureTableGrainDirectoryOptions>(name)))
                .AddSingletonNamedService<ILifecycleParticipant<ISiloLifecycle>>(name, (s, n) => (ILifecycleParticipant<ISiloLifecycle>)s.GetRequiredServiceByName<IGrainDirectory>(n));

            return services;
        }
    }
}
