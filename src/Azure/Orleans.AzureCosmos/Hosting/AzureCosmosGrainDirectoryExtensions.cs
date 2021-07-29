using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.AzureCosmos;
using Orleans.Configuration;
using Orleans.GrainDirectory;
using Orleans.Runtime;

namespace Orleans.Hosting
{
    public static class AzureCosmosGrainDirectoryExtensions
    {
        /// <summary>
        /// Configures the silo to use Azure Cosmos DB for the default grain directory.
        /// </summary>
        public static ISiloBuilder UseAzureCosmosGrainDirectoryAsDefault(this ISiloBuilder builder, Action<AzureCosmosGrainDirectoryOptions> configure)
            => builder.UseAzureCosmosGrainDirectory(GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY, configure);

        /// <summary>
        /// Configures the silo to use Azure Cosmos DB for the default grain directory.
        /// </summary>
        public static ISiloBuilder UseAzureCosmosGrainDirectoryAsDefault(this ISiloBuilder builder, Action<OptionsBuilder<AzureCosmosGrainDirectoryOptions>> configure)
            => builder.UseAzureCosmosGrainDirectory(GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY, configure);

        /// <summary>
        /// Configures the silo to use Azure Cosmos DB for grain directory.
        /// </summary>
        public static ISiloBuilder UseAzureCosmosGrainDirectory(this ISiloBuilder builder, string name, Action<AzureCosmosGrainDirectoryOptions> configure)
            => builder.ConfigureServices(services => Add(services.Configure(name, configure), name));

        /// <summary>
        /// Configures the silo to use Azure Cosmos DB for grain directory.
        /// </summary>
        public static ISiloBuilder UseAzureCosmosGrainDirectory(this ISiloBuilder builder, string name, Action<OptionsBuilder<AzureCosmosGrainDirectoryOptions>> configure)
            => builder.ConfigureServices(services =>
            {
                configure(services.AddOptions<AzureCosmosGrainDirectoryOptions>(name));
                Add(services, name);
            });

        private static void Add(this IServiceCollection services, string name)
        {
            services.ConfigureNamedOptionForLogging<AzureCosmosGrainDirectoryOptions>(name);
            services.AddSingletonNamedService(name, AzureCosmosGrainDirectory.Create)
                .AddSingletonNamedService(name, (s, n) => (ILifecycleParticipant<ISiloLifecycle>)s.GetRequiredServiceByName<IGrainDirectory>(n));
        }
    }
}
