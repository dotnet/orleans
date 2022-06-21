using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.GrainDirectory;

namespace Orleans.Hosting
{
    public static class AzureTableGrainDirectorySiloBuilderExtensions
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
    }
}
