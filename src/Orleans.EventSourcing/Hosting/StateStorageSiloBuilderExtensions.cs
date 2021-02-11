
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.EventSourcing;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.EventSourcing.StateStorage;

namespace Orleans.Hosting
{
    public static class StateStorageSiloBuilderExtensions
    {
        /// <summary>
        /// Adds a state storage log consistency provider as default consistency provider"/>
        /// </summary>
        public static ISiloHostBuilder AddStateStorageBasedLogConsistencyProviderAsDefault(this ISiloHostBuilder builder)
        {
            return builder.AddStateStorageBasedLogConsistencyProvider(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME);
        }

        /// <summary>
        /// Adds a state storage log consistency provider"/>
        /// </summary>
        public static ISiloHostBuilder AddStateStorageBasedLogConsistencyProvider(this ISiloHostBuilder builder, string name = "StateStorage")
        {
            return builder.ConfigureServices(services => services.AddStateStorageBasedLogConsistencyProvider(name));
        }

        /// <summary>
        /// Adds a state storage log consistency provider as default consistency provider"/>
        /// </summary>
        public static ISiloBuilder AddStateStorageBasedLogConsistencyProviderAsDefault(this ISiloBuilder builder)
        {
            return builder.AddStateStorageBasedLogConsistencyProvider(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME);
        }

        /// <summary>
        /// Adds a state storage log consistency provider"/>
        /// </summary>
        public static ISiloBuilder AddStateStorageBasedLogConsistencyProvider(this ISiloBuilder builder, string name = "StateStorage")
        {
            return builder.ConfigureServices(services => services.AddStateStorageBasedLogConsistencyProvider(name));
        }

        internal static IServiceCollection AddStateStorageBasedLogConsistencyProvider(this IServiceCollection services, string name)
        {
            services.TryAddSingleton<ILogViewAdaptorFactory>(sp => sp.GetServiceByName<ILogViewAdaptorFactory>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
            return services.AddSingletonNamedService<ILogViewAdaptorFactory, LogConsistencyProvider>(name);
        }
    }
}
