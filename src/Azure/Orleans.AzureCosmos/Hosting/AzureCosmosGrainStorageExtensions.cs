using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.AzureCosmos;
using Orleans.Configuration;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.Hosting
{
    public static class AzureCosmosGrainStorageExtensions
    {
        /// <summary>
        /// Configure silo to use Azure Cosmos DB as the default grain storage.
        /// </summary>
        public static ISiloBuilder UseAzureCosmosGrainStorageAsDefault(this ISiloBuilder builder, Action<AzureCosmosStorageOptions> configure)
            => builder.UseAzureCosmosGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configure);

        /// <summary>
        /// Configure silo to use Azure Cosmos DB for grain storage.
        /// </summary>
        public static ISiloBuilder UseAzureCosmosGrainStorage(this ISiloBuilder builder, string name, Action<AzureCosmosStorageOptions> configure)
            => builder.ConfigureServices(services => Add(services.Configure(name, configure), name));

        /// <summary>
        /// Configure silo to use Azure Cosmos DB as the default grain storage.
        /// </summary>
        public static ISiloBuilder UseAzureCosmosGrainStorageAsDefault(this ISiloBuilder builder, Action<OptionsBuilder<AzureCosmosStorageOptions>> configure)
            => builder.UseAzureCosmosGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configure);

        /// <summary>
        /// Configure silo to use Azure Cosmos DB for grain storage.
        /// </summary>
        public static ISiloBuilder UseAzureCosmosGrainStorage(this ISiloBuilder builder, string name, Action<OptionsBuilder<AzureCosmosStorageOptions>> configure)
            => builder.ConfigureServices(services =>
            {
                configure(services.AddOptions<AzureCosmosStorageOptions>(name));
                Add(services, name);
            });

        private static void Add(IServiceCollection services, string name)
        {
            services.ConfigureNamedOptionForLogging<AzureCosmosStorageOptions>(name);
            services.TryAddSingleton(sp => sp.GetServiceByName<IGrainStorage>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
            services.AddSingletonNamedService(name, AzureCosmosGrainStorage.Create)
                .AddSingletonNamedService(name, (s, n) => (ILifecycleParticipant<ISiloLifecycle>)s.GetRequiredServiceByName<IGrainStorage>(n));
        }
    }
}
