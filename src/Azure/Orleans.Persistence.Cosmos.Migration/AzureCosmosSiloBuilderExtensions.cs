using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.Persistence.Cosmos.Migration
{
    public static class AzureCosmosSiloBuilderExtensions
    {
        public static ISiloBuilder AddMigrationAzureCosmosGrainStorage(
            this ISiloBuilder builder,
            string name,
            Action<CosmosGrainStorageOptions> configureOptions)
        {
            builder.ConfigureServices(services =>
            {
                services.AddMigrationAzureCosmosGrainStorage(name, ob => ob.Configure(configureOptions));
            });
            return builder;
        }

        /// <summary>
        /// Configure silo to use Cosmos for grain storage.
        /// </summary>
        public static IServiceCollection AddMigrationAzureCosmosGrainStorage(this IServiceCollection services, string name,
            Action<OptionsBuilder<CosmosGrainStorageOptions>> configureOptions = null)
        {
            configureOptions?.Invoke(services.AddOptions<CosmosGrainStorageOptions>(name));
            services.AddTransient<IConfigurationValidator>(sp => new CosmosOptionsValidator<CosmosGrainStorageOptions>(sp.GetRequiredService<IOptionsMonitor<CosmosGrainStorageOptions>>().Get(name), name));
            services.ConfigureNamedOptionForLogging<CosmosGrainStorageOptions>(name);

            if (string.Equals(name, ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, StringComparison.Ordinal))
            {
                services.TryAddSingleton(sp => sp.GetServiceByName<IGrainStorage>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
            }

            return services.AddSingletonNamedService<IGrainStorage>(name, MigrationAzureCosmosGrainStorageFactory.Create)
                           .AddSingletonNamedService<ILifecycleParticipant<ISiloLifecycle>>(name, (s, n) => (ILifecycleParticipant<ISiloLifecycle>)s.GetRequiredServiceByName<IGrainStorage>(n));
        }
    }
}
